# Deferred Work

Pre-existing issues surfaced by reviews; not caused by the story that logged them.

> **Format is load-bearing — every entry MUST be a `### DW-<n>:` heading carrying a `status:` line.**
> `bmad-loop sweep` triage parses *only* entries with both, so an entry missing either is invisible to the
> burn-down and will never be swept (upstream bmad-code-org/bmad-loop#304). Full field spec:
> `.claude/skills/bmad-loop-sweep/deferred-work-format.md`. Append-only — never rewrite or delete an entry;
> close one by setting `status: done <date>` with a `resolution:` line.
>
> **2026-07-30 (A1-E11):** 160 flat `- source_spec:` appender bullets accumulated across Epics 7–11 were
> invisible to every sweep. They are migrated here as **DW-325 … DW-484**, with the original `summary` and
> `evidence` text preserved verbatim inside each `reason:` line. All six agent appender instructions
> (`bmad-dev-auto`, `bmad-quick-dev` ×4, `bmad-code-review`/`gds-code-review`) were changed the same day to
> emit canonical entries, so the flat shape cannot reappear.

## Design direction: MP disconnect resilience — AI takeover + reconnect (captured 2026-06-24, NOT yet scoped)

**Decision (Alec, 2026-06-24):** beyond Story 9.5's deterministic freeze-and-continue *floor*, Project Chimera should add (a) **host-toggleable AI takeover** of a dropped player's faction and (b) **player reconnect / rejoin**. Captured here as a direction — Alec parked scheduling; **not yet turned into PRD FRs or Epic 9 stories.** Surfaced from the 1.9a code-review conversation (relates to the disconnect-wedge deferral above).

**What EXISTS as a plan today:** only **Story 9.5** = deterministic freeze-and-continue (server dictates "faction K idle at applyTick", empty commands injected each tick, slot stays in the sim + checksum, tick-counted not wall-clock). A dropped player's units sit inert; **no AI, no rejoin.** The architecture flags "recoverable rejoin vs terminal" as an OPEN UX/arch decision (`game-architecture.md:2529`) but never scopes it; `reconnect`/`takeover` appear in **no** story.

**Why both are tractable (deterministic lockstep is the easy case):**
### DW-1: AI takeover = freeze-and-continue with AI commands instead of empty ones.
origin: migrated from legacy ledger ("Design direction: MP disconnect resilience — AI takeover + reconnect (captured 2026-06-24, NOT yet scoped)"), 2026-07-08
location: godot/src/AI/AiOpponentSystem.cs
reason: **AI takeover = freeze-and-continue with AI commands instead of empty ones.** `AiOpponentSystem` runs INSIDE the deterministic sim, so every peer computes identical AI moves with no extra netcode — no machine "hosts" the bot. Host-enable = a ruleset flag folded into `rulesetHash` (all peers agree on the rule). Same trigger as 9.5 (server-dictated at applyTick, ACK-gated, tick-counted). - **HARD PREREQUISITE:** `AiOpponentSystem` is **NOT deterministic today** — it uses `float`/`Math.*` (13 occurrences in `godot/src/AI/AiOpponentSystem.cs`; see also the 2026-06-09 deferral "Float math in the AI utility scorer"). It MUST be converted to `Fixed` before ANY AI runs in lockstep MP (takeover or otherwise). Shared prerequisite with the adaptive-AI work (Story 10.11). - Smaller: the AI must adopt a mid-game base/economy (it normally starts from scratch) — a new entry condition.
status: open
decision: 2026-07-25 Defer to a post-1.0 MP-resilience slice; keep open
decision: 2026-07-28 correct-course — keep open; post-1.0 MP-resilience slice; hard prerequisite Story 10.11 (AI float→Fixed)

### DW-2: Reconnect = replay the command log to catch up.
origin: migrated from legacy ledger ("Design direction: MP disconnect resilience — AI takeover + reconnect (captured 2026-06-24, NOT yet scoped)"), 2026-07-08
location: n/a
reason: **Reconnect = replay the command log to catch up.** The Epic-9 stateful server buffers the whole command stream; a rejoining client re-downloads it, fast-forward-simulates to the live tick, then resumes. Reuses the existing deterministic sim + `.chmr` replay machinery. v1 = replay-from-start (fine for short matches); v2 = periodic state snapshots + tail replay (needs a NEW save/restore of live SoA sim state — bigger lift).
status: open
decision: 2026-07-25 Scope now via correct-course — Add PRD FRs + Epic-9-style stories for v1 replay-from-start reconnect
decision: 2026-07-25 Scope now via correct-course — Add PRD FRs + Epic-9-style stories for v1 replay-from-start reconnect

**How other RTS solved it (reference):** WC3 / classic SC = freeze-and-continue (= our 9.5 floor; no takeover/rejoin). AoE2: Definitive Edition = added true reconnect (state restore). Supreme Commander / FAF = reconnect via replay-to-rejoiner + "Full Share" army-handoff (a takeover variant). Beyond All Reason (Spring engine) = server logs all commands, reconnect replays the log — the exact model Epic 9's server is becoming. Civ / Paradox = the AI-takeover poster child, but turn-based / server-authoritative (easier than real-time lockstep).

**Recommended shape when scheduled (Epic 9, after 9.5):** (1) prerequisite — make `AiOpponentSystem` deterministic (float→Fixed); (2) AI-takeover-on-drop (host ruleset flag; extends 9.5 to inject AI commands instead of empty ones); (3) reconnect / rejoin (command-log catch-up via the server buffer + replay; v1 replay-from-start, v2 snapshot+tail). Would also close the open `game-architecture.md:2529` rejoin decision. If wanted in 1.0 → needs PRD FRs + Epic 9 stories via `correct-course`; otherwise a clean post-1.0 MP-resilience slice. (Captured from the 1.9a review conversation; see memory [[chimera-mp-disconnect-ai-takeover-reconnect]].)

---
decision: 2026-07-28 correct-course — filed as Story 15.1 (Epic 15) + FR-79 (v1 replay-from-start reconnect)

## Deferred from: code review of story 1-10b (2026-06-24)

_Advisory-rule completeness/quality items from the `gds-code-review` of the determinism analyzer gate. All on **advisory** CHM rules (never release-gated), each with a runtime backstop (golden-checksum replay) — visible debt, none blocking 1.10b. The custom `BannedSimApiAnalyzer` rules were always scoped as best-effort vs. the architecture's "full" analyzer; these refine them._

### DW-3: CHM0002 enumeration detection is `foreach`-only
origin: migrated from legacy ledger ("Deferred from: code review of story 1-10b (2026-06-24)"), 2026-07-08
location: BannedSimApiAnalyzer.cs:135
reason: **CHM0002 enumeration detection is `foreach`-only** (`BannedSimApiAnalyzer.cs:135`). Misses `dict.Keys`/`.Values`, LINQ (`.Select`/`.First`/`.Aggregate`), and explicit `.GetEnumerator()` loops over a Dictionary/HashSet — the most common nondeterministic-iteration forms after `foreach`. Advisory; golden replay backstops actual order desync. Natural home: the future "full" custom analyzer pass.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-analyzer-coverage-hardening

### DW-4: CHM0001 misses fully-qualified `System.Single`/`System.Double` and `var`-inferred float
origin: migrated from legacy ledger ("Deferred from: code review of story 1-10b (2026-06-24)"), 2026-07-08
location: BannedSimApiAnalyzer.cs:119
reason: **CHM0001 misses fully-qualified `System.Single`/`System.Double` and `var`-inferred float** (`BannedSimApiAnalyzer.cs:119`). Only the `float`/`double` keyword (`PredefinedTypeSyntax`) fires; `System.Single x;` or `var x = 1f;` slip through, and RS0030 doesn't catch the declaration either. The XML-doc billing CHM0001 as "the real coverage" overclaims — tighten the doc or the rule.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-analyzer-coverage-hardening

### DW-5: CHM0003 misses `Span<T>.Sort` / delegate-reached sorts, and over-flags tie-broken (already-deterministic) sorts
origin: migrated from legacy ledger ("Deferred from: code review of story 1-10b (2026-06-24)"), 2026-07-08
location: BannedSimApiAnalyzer.cs:182-184
reason: **CHM0003 misses `Span<T>.Sort` / delegate-reached sorts, and over-flags tie-broken (already-deterministic) sorts** (`BannedSimApiAnalyzer.cs:182-184`). Scoped to direct `Array.Sort`/`List<T>.Sort` invocations. The one real finding (`ScenarioDirector.cs:206`) can't be cleared by passing a total-order comparer without also suppressing the rule.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-analyzer-coverage-hardening

### DW-6: CHM0004 magic-cap heuristic — false-positives on ordinary loop bounds/comparisons (`for (i<100)`, `if (hp>=50)`); blind to `static readonly` caps and negated bounds (`< -64`)
origin: migrated from legacy ledger ("Deferred from: code review of story 1-10b (2026-06-24)"), 2026-07-08
location: BannedSimApiAnalyzer.cs:200-276
reason: **CHM0004 magic-cap heuristic — false-positives on ordinary loop bounds/comparisons (`for (i<100)`, `if (hp>=50)`); blind to `static readonly` caps and negated bounds (`< -64`)** (`BannedSimApiAnalyzer.cs:200-276`). Documented as "Heuristic and advisory." Don't over-trust the 6-site baseline as "6 real caps." Cleanup story (Epics 2/7 → `SimConstants`) will triage.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-analyzer-coverage-hardening

### DW-7: Analyzer test hardening
origin: migrated from legacy ledger ("Deferred from: code review of story 1-10b (2026-06-24)"), 2026-07-08
location: ProjectChimera.Analyzers.Tests/BannedSimApiAnalyzerTests.cs
reason: **Analyzer test hardening** (`ProjectChimera.Analyzers.Tests/BannedSimApiAnalyzerTests.cs`). `OrderBy_does_not_report_CHM0003` is structurally vacuous (CHM0003 can never match `OrderBy`, so it proves nothing). Positive CHM0001 coverage pins only a field + a cast; add `float?`, `List<float>`, tuple-element, and lambda-param forms (the bulk of the 128 advisory sites) so a future keyword-context regression is caught.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-analyzer-coverage-hardening

### DW-8: CI release-gate `github.event.inputs.run_release_gate == 'true'` is correct only because GitHub serializes dispatch inputs as strings
origin: migrated from legacy ledger ("Deferred from: code review of story 1-10b (2026-06-24)"), 2026-07-08
location: .github/workflows/determinism-gate.yml
reason: **CI release-gate `github.event.inputs.run_release_gate == 'true'` is correct only because GitHub serializes dispatch inputs as strings** (`.github/workflows/determinism-gate.yml`). Add a guard comment so a well-meaning `== true` "cleanup" can't silently make the on-demand release proof unreachable while still appearing wired.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-analyzer-coverage-hardening

**Also raised in the same review (NOT deferred — tracked on the story):** the float↔string RS0030 bans not firing (decision pending) and the CHM0005 name-only converter allow-list (patch) live in story 1-10b's `### Review Findings`.

## Deferred from: dev of story-3.4 (2026-07-06)

_gds-dev-story [claude-opus-4-8], baseline `f7a54ef`. All 10 tasks + 6 ACs; PURE AUTHORING-TIME zero-fold (stamps 9/3/1/2 + StartStateHash 1, 18 goldens byte-identical, release gate 0-err/RS0030-clean); Tier-1 761 pass/1 skip/0 fail (+45 new). `/godot-verify` PASS on all AC6. Items surfaced during dev, not blockers:_

### DW-9: The 6-archetype closed set is duplicated across `UnitDefinitionValidator._categories`, `BehaviorRegistry._archetypes`, and the validator's error string, so a future 7th `UnitCategory` can be added to one and missed in another (a behavior listing the new archetype would then be silently dropped at load).
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-6-archetype-orthogonal-ability-behavior-composition-no-subclassing.md`
location: UnitDefinitionValidator.cs:60
reason: summary: The 6-archetype closed set is duplicated across `UnitDefinitionValidator._categories`, `BehaviorRegistry._archetypes`, and the validator's error string, so a future 7th `UnitCategory` can be added to one and missed in another (a behavior listing the new archetype would then be silently dropped at load). evidence: Three independent literals of the same Worker/Melee/Ranged/Siege/Air/Structure set exist (UnitDefinitionValidator.cs:60, BehaviorRegistry.cs archetype set, UnitDefinitionValidator.cs error string) with no shared source; `UnitCategory` (Core/UnitCategory.cs) is the canonical enum they should derive from. summary: The in-editor composition UI (chips/Add picker `AddComponentPicker`, preset dropdown `AddCompositionRow`, undo closures `ApplyComponentList`) has no automated verification — it is Godot-`Control` code not loadable in the Tier-1 assembly, so the array-mutation/undo/preset-filter wiring is verified only by live in-engine driving between sessions. evidence: A grep of ProjectChimera.Sim.Tests for `UnitCardPanel`/`AddComponentPicker`/`AddCompositionRow` returns no hits; only the pure `Bundle`/`Detect`/validator helpers are Tier-1-tested. Recommend extracting the array-mutation/undo logic into a Godot-free seam or adopting a scripted godot-mcp editor-driving check as the verification of record.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-unit-category-single-source

### DW-10: Creation-suite editor panels do not rebind their held `ScenarioData` after a scenario is reloaded/imported at runtime — each captures `_scenario` (and its write-back path) once at `Initialize` and never again, so after a reload the panel edits a stale object and can save it back to the originally-captured path (silent data loss / wrong-target save).
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-8-persistence-manifest-authoring-which-attributes-carry-forward-through-the-validate-gate.md`
location: n/a
reason: summary: Creation-suite editor panels do not rebind their held `ScenarioData` after a scenario is reloaded/imported at runtime — each captures `_scenario` (and its write-back path) once at `Initialize` and never again, so after a reload the panel edits a stale object and can save it back to the originally-captured path (silent data loss / wrong-target save). evidence: `PersistenceManifestPanel.SetScenario` (and the identically-shaped `TriggerEditorPanel.SetScenario`) exist but have zero callers repo-wide; the panels bind `_scenario` once in `Initialize`/`PersistenceManifestPhase`. This is a pre-existing cross-editor architectural gap surfaced (not caused) by Story 3.8 — the proper fix wires every editor's `SetScenario` into the scenario (re)load path (e.g. `MapGeneratorPanel.OnLoadRequested` / `ScenarioLoadPhase`), which is out of a single manifest-authoring story's scope.
status: open
decision: 2026-07-28 correct-course — bundle editor-panel-scenario-rebind absorbed into scenario-reapply-slot-faction-def-refresh (DW-229; Epic 15, Story 15.6)

### DW-11: HeroStore is additive across re-deploys with no clear on return-to-Edit; a re-deployed profile would leave stale live hero rows in the store (and hash).
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-9-offline-hero-persistence-rail-save-load-hero-picker-deterministic-init-time-apply.md`
location: HeroStore.cs:126
reason: summary: HeroStore is additive across re-deploys with no clear on return-to-Edit; a re-deployed profile would leave stale live hero rows in the store (and hash). evidence: HeroStore.Mint (HeroStore.cs:126) is purely additive and MainScene.ResetMatchOnReturnToEdit never touches Host.Heroes. Not reachable in Story 3.9 (no in-session re-deploy path — the F5 Edit↔Play loop is Story 3.10), and match-state reset including the hero store is Story 3.10's defined scope.
status: done 2026-07-08
resolution: already resolved: Edit↔Play reset now clears the HeroStore: SimulationHost.ClearForReset() calls Heroes.Clear() (SimulationHost.cs:237), HeroStore.Clear() bulk-wipes all arrays + free-list (HeroStore.cs:253-276), and ResetToAuthoredStart calls _host.ClearForReset() before the non-additive re-mint (MainScene.cs:1254). Test asserts LiveHeroCount==1 after round-trips (SimResetTests.cs:302). Story 3.10 (its named scope) is done.

### DW-12: On-disk PlayerProfile level/xp values are minted into HeroStore with no range/cap validation — a cheat/invalid-state vector once a runtime consumer exists.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-9-offline-hero-persistence-rail-save-load-hero-picker-deterministic-init-time-apply.md`
location: profiles.json
reason: summary: On-disk PlayerProfile level/xp values are minted into HeroStore with no range/cap validation — a cheat/invalid-state vector once a runtime consumer exists. evidence: HeroProfileLoader.LoadInto trusts the raw ints in profiles.json verbatim; a hand-edited negative level or absurd xp is minted and folded into StartStateHash. Gameplay-inert until Story 3.13 consumes level/xp (determinism holds regardless), so the fail-closed value gate belongs with that consumer.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-hero-profile-load-hardening

### DW-13: The applied hero profile is not slot/owner-scoped — LoadInto mints into the first matching placed hero regardless of which player slot owns it.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-9-offline-hero-persistence-rail-save-load-hero-picker-deterministic-init-time-apply.md`
location: n/a
reason: summary: The applied hero profile is not slot/owner-scoped — LoadInto mints into the first matching placed hero regardless of which player slot owns it. evidence: HeroProfileLoader.LoadInto matches on UnitId == HeroDefId across all placed heroes (lowest entity id wins; duplicates skip via Mint -1). In a mirror/multi-slot scenario the deploying human's hero is assumed to be the first placed. Ownership scoping is unspecified by the intent and gameplay-inert today.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-hero-profile-load-hardening
decision: 2026-07-17 Scope to owning slot — Thread the owning player slot into LoadInto and mint only into that slot's placed hero.
decision: 2026-07-16 Scope to owning slot — Thread the owning player slot into LoadInto and mint only into that slot's placed hero.

### DW-14: The boot-time StartStateHash is the empty-store value; the deployed-hero hash is recomputed only at Launch and GD.Print-logged, never put on the wire.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-9-offline-hero-persistence-rail-save-load-hero-picker-deterministic-init-time-apply.md`
location: n/a
reason: summary: The boot-time StartStateHash is the empty-store value; the deployed-hero hash is recomputed only at Launch and GD.Print-logged, never put on the wire. evidence: MainScene computes StartStateHash at _Ready with a null PendingHeroProfile (empty store); HeroPickerPhase.Launch recomputes post-mint but only logs it. Consistent with 3.2's D-3 (StartStateHash off the wire until Epic 9). When Epic 9 wires it into the attested multi-hash handshake it must consume the post-mint (init-time/Launch-time) value, or the deployed hero is silently omitted.
status: done 2026-07-24
resolution: already resolved: MainScene.cs:538-567 — HeroProfileLoader.LoadInto mints the deployed profile then MatchAgreementHash.Compute folds the post-mint StartStateHash onto the Ready packet (LobbyUi.cs:418); Story 9.16.

### DW-15: The preserve-hero-progress branch of the Edit↔Play reset always re-mints hero.level + hero.xp, ignoring which attributes the scenario's Story-3.8 PersistenceManifest actually selects to carry forward.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: The preserve-hero-progress branch of the Edit↔Play reset always re-mints hero.level + hero.xp, ignoring which attributes the scenario's Story-3.8 PersistenceManifest actually selects to carry forward. evidence: MainScene.ResetToAuthoredStart's preserve path builds a snapshot PlayerProfile with hardcoded hero.level/hero.xp Values, not manifest.DeriveProfileShape() keys. Inert pre-3.13 (only level/xp are eligible today and xp doesn't grow at runtime), so it is gameplay-neutral now; the moment Story 3.13 adds runtime XP or the manifest carries more eligible attributes, the preserve path must route through the manifest shape (the same seam BuildProfile uses on Save).
status: done 2026-07-19
resolution: resolved by sweep bundle dw-hero-profile-load-hardening

### DW-16: The preserve-hero-progress branch silently loses a hero's progress if that hero died during the playtest (no live HeroStore row to snapshot), falling through to the authored base values.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: The preserve-hero-progress branch silently loses a hero's progress if that hero died during the playtest (no live HeroStore row to snapshot), falling through to the authored base values. evidence: The snapshot loop reads live HeroStore rows keyed by HeroId before ClearForReset; a dead hero has no live row, so persistence-test mode reverts it to the authored base. Hero death/revival is Story 3.14 and the preserve path is inert pre-3.13, so this is unreachable today; the dead-hero preserve semantics should be defined when 3.14 lands.
status: done 2026-07-08
resolution: already resolved: Story 3.14 (done) fixed this: a hero that dies during playtest keeps its persisted row — HeroXpSystem sets AwaitingRevival[slot]=true (HeroXpSystem.cs:163) and never calls Heroes.Destroy, so Alive[slot] stays true and the harvest snapshot guard (MainScene.cs:1219) still finds it. Progress no longer lost.

### DW-17: When Epic 9 adds match-seed plumbing / non-default replay seeds, the in-place reset must reseed the RNG to the live match seed, not the hardcoded DEFAULT_RNG_SEED.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: When Epic 9 adds match-seed plumbing / non-default replay seeds, the in-place reset must reseed the RNG to the live match seed, not the hardcoded DEFAULT_RNG_SEED. evidence: EntityWorld.Clear reseeds to DEFAULT_RNG_SEED, which is correct today because no path reseeds the world to a non-default seed (the live replay/online transitions are now gated OUT of the reset entirely). The MP-seed handshake (Epic 9) will introduce a live match seed; the reset (offline-only today) must then capture and restore it, or a seeded reset would diverge from the seeded stream.
status: open
decision: 2026-07-28 correct-course — bundle match-seed-plumbing with DW-225 (Epic 15, Story 15.5); do not close independently

### DW-18: An in-place reset collapses every store's live/high-water count to 0 then repopulates within one frame; a presentation bridge that caches an instance count (rather than reading HighWaterMark each frame) could leave ghost MultiMesh instances after an Edit↔Play round-trip.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: An in-place reset collapses every store's live/high-water count to 0 then repopulates within one frame; a presentation bridge that caches an instance count (rather than reading HighWaterMark each frame) could leave ghost MultiMesh instances after an Edit↔Play round-trip. evidence: ClearForReset zeroes AliveCount/HighWaterMark then the re-apply repopulates, a count-collapse no prior code path produced. The bridges read capture-once store references and recompute from HighWaterMark each frame (so they should reconcile), but this is presentation, untested at Tier-1, and belongs on the /godot-verify manual checklist for the F5 loop.
status: done 2026-07-08
resolution: already resolved: MultiMeshBridge reads world.HighWaterMark fresh each frame (MultiMeshBridge.cs:143), recomputes _counts from the live sim (146-151), and reconciles InstanceCount whenever it differs from _lastCount (155-160), so the transient reset collapse-to-0 is picked up next frame — no ghost MultiMesh instances persist.

### DW-19: The cleared-store==freshly-constructed test compares only ~11 of EntityWorld's ~60 SoA arrays, so a future SoA field that is folded/read but omitted from EntityWorld.Clear (and not re-defaulted by ScenarioApplier) would not be caught by the reset tests.
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: The cleared-store==freshly-constructed test compares only ~11 of EntityWorld's ~60 SoA arrays, so a future SoA field that is folded/read but omitted from EntityWorld.Clear (and not re-defaulted by ScenarioApplier) would not be caught by the reset tests. evidence: ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed asserts a representative subset; the byte-identical reproduce-run test masks omissions because Create/ApplyUnitDefinition re-defaults non-def per-entity fields on re-apply. EntityWorld.Clear is field-complete today (verified by two review layers); an exhaustive field-by-field fresh==cleared sweep would self-pin it against future field additions.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-reset-determinism-test-coverage

## Deferred from: follow-up code review of story-3.10 (2026-07-07)

### DW-20: No reset-reproduction test exercises a run that actually fights — the determinism keystone reproduces the golden applier scenario but never spawns projectiles or casts abilities before/after the reset, so a future per-match field held by a combat/projectile/ability system (not a store) could desync uncaught.
origin: migrated from legacy ledger ("Deferred from: follow-up code review of story-3.10 (2026-07-07)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: No reset-reproduction test exercises a run that actually fights — the determinism keystone reproduces the golden applier scenario but never spawns projectiles or casts abilities before/after the reset, so a future per-match field held by a combat/projectile/ability system (not a store) could desync uncaught. evidence: ClearForReset resets stores + the AI latch, but tick systems (CombatSystem._spatialHash, ProjectileSystem, AbilityCastSystem, MovementSystem._neighborBuffer, EffectExecutor.LastPeakStackDepth) hold instance state it never touches. These are per-tick-rebuilt or diagnostic-only today so no test fails — the gap is that a newly-folded per-match system field would pass every existing reset test. A reproduce-run test over a scenario that spawns projectiles + casts abilities would pin the composed combat path the current keystone skips.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-reset-determinism-test-coverage

### DW-21: ScenarioApplier.ApplyFallback does not clear _lastAppliedHeroes (only Apply does), so the fallback reset path re-mints against whatever hero records a prior Apply left — an asymmetry the new reset newly depends on.
origin: migrated from legacy ledger ("Deferred from: follow-up code review of story-3.10 (2026-07-07)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: ScenarioApplier.ApplyFallback does not clear _lastAppliedHeroes (only Apply does), so the fallback reset path re-mints against whatever hero records a prior Apply left — an asymmetry the new reset newly depends on. evidence: The reset re-mint reads ScenarioApplier.LastAppliedHeroes after re-applying; Apply clears the list at its top but ApplyFallback does not, and ClearForReset does not touch it (it lives on the applier, not a store). Safe today because a given applier consistently uses one path per session (fallback path never had a prior Apply populate the list), so the list is empty in the fallback reset. A one-line clear at the top of ApplyFallback (or in ClearForReset) would make the invariant symmetric and future-proof against any mixed Apply→ApplyFallback flow.
status: done 2026-07-19
resolution: already resolved: Story 7.7 retired the separate ApplyFallback writer — ScenarioApplier.cs:386-389 documents every fallback boot now routes Apply(Validate(BuildFallbackMirror()).Value); Apply clears _lastAppliedHeroes at :142, so the asymmetry is gone (SimResetTests.cs:507-509 confirms fallback goes through the validated Apply path).

### DW-22: The offlineEditorLoop guard that gates the destructive reset away from online/replay transitions has zero automated coverage — the highest-blast-radius decision in the change is verified only by inspection.
origin: migrated from legacy ledger ("Deferred from: follow-up code review of story-3.10 (2026-07-07)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-10-added-edit-play-round-trip-loop-no-restart-playtest.md`
location: n/a
reason: summary: The offlineEditorLoop guard that gates the destructive reset away from online/replay transitions has zero automated coverage — the highest-blast-radius decision in the change is verified only by inspection. evidence: WinConditionPhase's ModeChanged handler routes ClearForReset+re-apply only when `_ctx.ReplayPlayer == null && !_ctx.Lockstep.IsOnline`; a regression that lets it fire during a live match/replay re-seeds the RNG to DEFAULT_RNG_SEED mid-session → lockstep desync / clobbered replay seed. The guard is correct today (ordering verified: GoOnline sets IsOnline and TryLoadReplay assigns ReplayPlayer before SetMode(Play)), but no test constructs WinConditionPhase/GameState or drives ModeChanged — the whole handler is Godot-coupled and this repo has no Godot integration-test project. Extracting the `(isOnline, hasReplay, targetMode) → shouldReset` decision into a pure static predicate would make this guard Tier-1 testable without a Godot harness; until then it is on the /godot-verify manual checklist.
status: open

## Deferred from: code review of story-3.11 (2026-07-07)

### DW-23: The kit-bootstrap (`EnsureKitInitialized`) and the `Heading`/`Body` label helpers are copy-pasted across `MainMenuOverlay`, `SettingsPanel`, and `HeroPickerOverlay` with minor drift — consolidate into a shared static helper or a base overlay type.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.11 (2026-07-07)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-11-added-apply-the-design-system-to-the-front-end-shell-title-mode-select-settings.md`
location: n/a
reason: summary: The kit-bootstrap (`EnsureKitInitialized`) and the `Heading`/`Body` label helpers are copy-pasted across `MainMenuOverlay`, `SettingsPanel`, and `HeroPickerOverlay` with minor drift — consolidate into a shared static helper or a base overlay type. evidence: Three near-identical copies now exist and have already diverged (e.g. `SettingsPanel.Body` forces `SizeFlagsVertical = ShrinkCenter`, `MainMenuOverlay.Body` does not, and `HeroPickerOverlay`'s helper takes a different parameter set), so the styling of "the same" element differs by consumer. This is a pre-existing per-overlay pattern that Story 3.11 extended by two copies; every new kit-consuming overlay repeats it and risks further drift. A shared `ChimeraOverlayBase` (or static helpers in the kit) owning bootstrap + Heading/Body would make the pattern single-sourced. Flagged by the Blind Hunter review layer.
status: open

## Deferred from: code review of story-3.12 (2026-07-07)

### DW-24: Editor delete+undo of a placed unit (`EntityPlacer.RestoreUnit` / `UnitSnapshot`) does not carry the new `Delivery`/`ProjectileSpeed` fields, so undoing a deleted Projectile unit silently reverts it to the Create-default Hitscan (losing its custom projectile speed).
origin: migrated from legacy ledger ("Deferred from: code review of story-3.12 (2026-07-07)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-12-authorable-attack-delivery-flag-hitscan-vs-projectile-per-unit-projectile-speed.md`
location: EntityPlacer.cs:90-107
reason: summary: Editor delete+undo of a placed unit (`EntityPlacer.RestoreUnit` / `UnitSnapshot`) does not carry the new `Delivery`/`ProjectileSpeed` fields, so undoing a deleted Projectile unit silently reverts it to the Create-default Hitscan (losing its custom projectile speed). evidence: `UnitSnapshot` (EntityPlacer.cs:90-107) captures AttackRange/SplashRadius etc. but not Delivery/ProjectileSpeed, and `RestoreUnit` (EntityPlacer.cs:1099-1127) calls `Create` then restores fields directly without `ApplyUnitDefinition`. This is the SAME pre-existing incomplete-snapshot class already documented at EntityPlacer.cs:1122 (collision_radius / separation_priority / category / attack_domains / tags / energy are ALL dropped on restore today) and explicitly chartered to Story 3.17 ("widen UnitSnapshot to full authored state"). Story 3.12 extends the gap by two fields; the intent's editor-reversibility (AC5) is satisfied by the Unit Card Editor form-undo (EditorHistory), a distinct path that IS implemented and pattern-verified. Fixing piecemeal here (2 of ~8 dropped fields) would be an inconsistent band-aid; 3.17 closes the whole class. Flagged by the Blind Hunter + Edge Case review layers.
status: done 2026-07-08
resolution: already resolved: Closed by Story 3.17 (done): UnitSnapshot now carries the source def (UnitSnapshot.cs:27 `public UnitDefinition? Def;`, set in EntityWorld.SnapshotUnit:897), and RestoreUnit routes a def-based unit through ApplyUnitDefinition (EntityWorld.cs:937-942) which sets Delivery/ProjectileSpeed (EntityWorld.cs:820-821). Palette-placed combat units always carry a def (EntityPlacer.cs:422-426), so the chartered Projectile-unit case is restored correctly.

## Deferred from: follow-up code review of story-3.12 (2026-07-07)

### DW-25: A high authored `projectile_speed` (anything up to the validator's loose 32768 ceiling) makes a projectile overshoot the 0.5-unit hit radius every tick and never converge — `ProjectileSystem.Tick` has no snap-to-goal clamp and no max-lifetime/TTL — so the shell orbits its target forever, permanently leaking its `MAX_PROJECTILES` slot until the pool fills and the unit stops firing.
origin: migrated from legacy ledger ("Deferred from: follow-up code review of story-3.12 (2026-07-07)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-12-authorable-attack-delivery-flag-hitscan-vs-projectile-per-unit-projectile-speed.md`
location: ProjectileSystem.cs:88-105
reason: summary: A high authored `projectile_speed` (anything up to the validator's loose 32768 ceiling) makes a projectile overshoot the 0.5-unit hit radius every tick and never converge — `ProjectileSystem.Tick` has no snap-to-goal clamp and no max-lifetime/TTL — so the shell orbits its target forever, permanently leaking its `MAX_PROJECTILES` slot until the pool fills and the unit stops firing. evidence: `ProjectileSystem.Tick` (ProjectileSystem.cs:88-105) checks `distSqr <= HIT_SQR` (0.25) BEFORE advancing, then advances by the full `dir * Speed[i] * dt` with no clamp to the remaining distance and no lifetime cap. At the old hardcoded speed 18 (0.6 u/tick) the max overshoot is 0.1 < 0.5, so it always converged; making speed authorable (Story 3.12) up to 32768 exposes the latent non-convergence for any speed whose per-tick step can exceed the hit radius on the final approach (roughly speed > ~30, and non-convergent for plausible authored values like 40-60 depending on approach geometry). The proper fix (snap-to-goal clamp on the advance, and/or a sane speed cap + projectile TTL) is a projectile-TRACKING change — excluded by this story's intent boundary ("no changes to projectile visuals/tracking beyond honouring per-unit speed") — and would move impact positions/timing enough to require a full golden re-baseline, so it belongs in its own focused change. Flagged by the Blind Hunter review layer.
status: open

## Deferred from: code review of story-3.13 (2026-07-08)

### DW-26: The shipped hero-centric `HeroDefinition.XpPerKill` (`xp_per_kill`, default 100) is superseded by the victim-centric `UnitDefinition.XpBounty` (Story 3.13's runtime XP source) but is still validated, round-tripped by `FactionWriter`, and surfaced in the Unit Card Editor as a functional "XP per kill" knob that the runtime no longer consumes — a misleading authoring surface.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.13 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-13-heroxpsystem-kill-credit-xp-leveling-stat-growth-runtime.md`
location: UnitDefinitionValidator.cs
reason: summary: The shipped hero-centric `HeroDefinition.XpPerKill` (`xp_per_kill`, default 100) is superseded by the victim-centric `UnitDefinition.XpBounty` (Story 3.13's runtime XP source) but is still validated, round-tripped by `FactionWriter`, and surfaced in the Unit Card Editor as a functional "XP per kill" knob that the runtime no longer consumes — a misleading authoring surface. evidence: `HeroXpSystem` credits `victim.XpBounty` only (grep: no `XpPerKill` reference in the Combat runtime). `HeroDefinition.XpPerKill` remains authored/validated (`UnitDefinitionValidator.cs` hero.xp_per_kill rule) and editor-exposed. Story 3.13 D5 deliberately left it untouched (removing it would perturb Story 3.7's validator/editor/writer/tests); the clean reconciliation (remove or repurpose the field + its editor/validator/writer surface) is a focused follow-up. Flagged by the Intent-Alignment review layer (Divergence 2).
status: open
decision: 2026-07-25 Repurpose it — Give it runtime meaning again — a per-hero XP-gain multiplier or bounty override layered on victim XpBounty
decision: 2026-07-25 Repurpose it — Give it runtime meaning again — a per-hero XP-gain multiplier or bounty override layered on victim XpBounty
decision: 2026-07-08 Repurpose it — Give xp_per_kill runtime meaning again — e.g. a per-hero XP-gain multiplier or a per-hero bounty override layered onto the victim's XpBounty — so the authoring knob is no longer misleading.
decision: 2026-07-28 correct-course — bundle hero-xp-per-kill-repurpose (Epic 15, Story 15.9)

### DW-27: The end-of-match harvest of live `HeroStore.Level`/`Xp` into the deployed `PlayerProfile` (`MainScene.ResetToAuthoredStart` capture) and the picker Save/Overwrite rewire (`HeroPickerOverlay.ResolveHeroProgress`) have NO automated coverage — a wrong-way change (e.g. dropping the harvested value and re-persisting the level-1/0 placeholder) would silently regress AC3 with the whole suite green.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.13 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-13-heroxpsystem-kill-credit-xp-leveling-stat-growth-runtime.md`
location: MainScene.cs
reason: summary: The end-of-match harvest of live `HeroStore.Level`/`Xp` into the deployed `PlayerProfile` (`MainScene.ResetToAuthoredStart` capture) and the picker Save/Overwrite rewire (`HeroPickerOverlay.ResolveHeroProgress`) have NO automated coverage — a wrong-way change (e.g. dropping the harvested value and re-persisting the level-1/0 placeholder) would silently regress AC3 with the whole suite green. evidence: These live in `src/UI`/`MainScene` (Godot-coupled Tier-2), outside the Godot-free `ProjectChimera.Sim.Tests`; grep for `ResolveHeroProgress`/`Harvested`/`HeroPickerOverlay` across the test project returns nothing. The Tier-1 `HeroXpTests.Discard_ReMintsAuthoredValues` covers the discard branch, and `HeroProfilePersistenceTests` covers `BuildProfile`+manifest-shape, but the live-HeroStore→profile harvest capture (plain data logic at `MainScene.cs:~1198`) is not lifted into a testable seam. Extracting a Godot-free harvest resolver + unit-testing "Has → uses harvested, not fallback" would close it. Flagged by the Verification-Gap review layer.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-hero-harvest-resolver-extraction

### DW-28: A pathological large `Base*` stat combined with (validator-capped) per-level growth can still overflow an `Effective*` stat — the pre-existing unsaturated `ModifierSystem.AccumulateBonus` (`Fixed +=`, no clamp) effective-stat accumulation class, not unique to hero growth. Story 3.13 capped the growth CONTRIBUTION (`*_per_level < 256`, ≤99 stacks) so realistic authoring is safe; the residual requires an already-extreme base.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.13 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-13-heroxpsystem-kill-credit-xp-leveling-stat-growth-runtime.md`
location: n/a
reason: summary: A pathological large `Base*` stat combined with (validator-capped) per-level growth can still overflow an `Effective*` stat — the pre-existing unsaturated `ModifierSystem.AccumulateBonus` (`Fixed +=`, no clamp) effective-stat accumulation class, not unique to hero growth. Story 3.13 capped the growth CONTRIBUTION (`*_per_level < 256`, ≤99 stacks) so realistic authoring is safe; the residual requires an already-extreme base. evidence: `ModifierSystem.RecomputeEntity` sums `Base + Σ modifier deltas` with a zero-floor but no ceiling; any large modifier stack (hero growth, or authored buffs) can exceed the 16.16 Fixed range. A general saturation clamp on the effective-stat recompute (or a Base+modifier authoring bound) would close the whole class deterministically. Flagged by the Edge Case review layer (finding #4 residual).
status: open
seen-again: 2026-07-28 (correct-course verification — overflow half of legacy story-2.2a review item 2)

## Deferred from: follow-up code review of story-3.13 (2026-07-08)

### DW-29: Hero stat growth is tracked by a per-row COUNT (`HeroStore.GrowthStacksApplied`), not by presence-of-modifier on the live entity, so when Story 3.14 revives a hero onto a NEW entity (which carries no modifiers) `ReconcileGrowth` will early-return (`desired = Level-1 <= GrowthStacksApplied`) and the revived hero silently gets ZERO stat growth — a level-N hero fighting with level-1 stats.
origin: migrated from legacy ledger ("Deferred from: follow-up code review of story-3.13 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-13-heroxpsystem-kill-credit-xp-leveling-stat-growth-runtime.md`
location: n/a
reason: summary: Hero stat growth is tracked by a per-row COUNT (`HeroStore.GrowthStacksApplied`), not by presence-of-modifier on the live entity, so when Story 3.14 revives a hero onto a NEW entity (which carries no modifiers) `ReconcileGrowth` will early-return (`desired = Level-1 <= GrowthStacksApplied`) and the revived hero silently gets ZERO stat growth — a level-N hero fighting with level-1 stats. evidence: `HeroXpSystem.ReconcileGrowth` returns when `desired <= GrowthStacksApplied`; `GrowthStacksApplied` persists on the `HeroStore` row across a revival while the growth `Modifier` lives on the (destroyed) entity's `ModifierStore` slot. Story 3.13 is single-entity-per-hero so this is correct today (no revival exists — revival is explicitly out of 3.13 scope, and the reserved 3.14 fields are folded-at-default only). But 3.14's revival must reset `GrowthStacksApplied` to 0 (or reconcile against actual modifier presence on the new entity) at re-mint, or the whole growth stack is dropped for every revived hero. Captured as a binding obligation for Story 3.14. Flagged by the Blind Hunter review layer (follow-up finding #3).
status: done 2026-07-08
resolution: already resolved: Obligation fulfilled by Story 3.14 (done): HeroXpSystem.RespawnHero resets _heroes.GrowthStacksApplied[slot]=0 (HeroXpSystem.cs:231) then calls ReconcileGrowth (line 248), re-applying Level-1 growth stacks onto the fresh revived entity — revived hero regains full stat growth.

## Deferred from: code review of story-3.14 (2026-07-08)

### DW-30: Revival respawns a FRESH `EntityWorld` entity from the hero's `SourceDef`, restoring only Level/Xp/growth onto the persisted `HeroStore` row — so when Story 3.15 adds items/inventory, any inventory hung off the ENTITY (rather than the persisted hero row) will be silently dropped on every revival, violating AC1/AC2's "items retained".
origin: migrated from legacy ledger ("Deferred from: code review of story-3.14 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-14-hero-death-revival.md`
location: n/a
reason: summary: Revival respawns a FRESH `EntityWorld` entity from the hero's `SourceDef`, restoring only Level/Xp/growth onto the persisted `HeroStore` row — so when Story 3.15 adds items/inventory, any inventory hung off the ENTITY (rather than the persisted hero row) will be silently dropped on every revival, violating AC1/AC2's "items retained". evidence: `HeroXpSystem.RespawnHero` calls the spawn delegate (`world.Create` + `ApplyUnitDefinition`) and re-links `EntityId`/`HeroIndex` + resets `GrowthStacksApplied`, but nothing carries per-entity inventory across the death→respawn gap; item state does not exist yet (Story 3.15). This is the exact mirror of the 3.13→3.14 `GrowthStacksApplied` obligation: 3.15 must store hero inventory on the persisted `HeroStore` row (survives by construction) OR have revival explicitly re-attach it to the new entity. Binding obligation for Story 3.15. Flagged by the Intent-Alignment review layer.
status: done 2026-07-08
resolution: already resolved: Obligation fulfilled by Story 3.15 (done): hero inventory lives on the persisted HeroStore row (HeroStore.Inventory flat ring, HeroStore.cs:144-153), and RespawnHero re-LINKS the existing row to the new entity (EntityId[slot]=newEntity) without re-Minting, so inventory refs survive death→revival by construction (documented HeroStore.cs:144-149).

### DW-31: The command-card revive buttons render only in the `!canProduce` branch (`CommandCardSystem.RefreshCard`), so a building that BOTH produces units AND is flagged `revives_heroes` exposes no revive button in-UI — AC2's canonical "eligible production building" has no affordance for a dual-capability building (the sim + a dedicated non-producing revive building both work).
origin: migrated from legacy ledger ("Deferred from: code review of story-3.14 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-14-hero-death-revival.md`
location: CommandCardSystem.cs
reason: summary: The command-card revive buttons render only in the `!canProduce` branch (`CommandCardSystem.RefreshCard`), so a building that BOTH produces units AND is flagged `revives_heroes` exposes no revive button in-UI — AC2's canonical "eligible production building" has no affordance for a dual-capability building (the sim + a dedicated non-producing revive building both work). evidence: `CommandCardSystem.cs` gates `RefreshReviveButtons` on `!canProduce && RevivesHeroes[bId]`; the revive grid reuses the (hidden) train grid, so surfacing both requires UI-layout work. The sim path (`ReviveHeroCommand`/`OrderApplier`) is production-agnostic and fully general; only the presentation of dual producer+reviver buildings is narrowed. Presentation-only, headless-unverifiable in this environment. Flagged by the Intent-Alignment + Blind Hunter review layers.
status: open

### DW-32: AC3's "manifest-persisted attributes still finalize per FR-7a" for a fallen (disabled-revival or awaiting) hero has no Tier-1 end-to-end coverage — the harvest lives in the Godot-coupled `MainScene.ResetToAuthoredStart`/`HeroPickerOverlay`, outside the Godot-free test project. Only the sim precondition (the row stays `HeroStore.Alive` so it remains harvestable) is asserted.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.14 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-14-hero-death-revival.md`
location: n/a
reason: summary: AC3's "manifest-persisted attributes still finalize per FR-7a" for a fallen (disabled-revival or awaiting) hero has no Tier-1 end-to-end coverage — the harvest lives in the Godot-coupled `MainScene.ResetToAuthoredStart`/`HeroPickerOverlay`, outside the Godot-free test project. Only the sim precondition (the row stays `HeroStore.Alive` so it remains harvestable) is asserted. evidence: `HeroRevivalTests` assert `Heroes.Alive[slot]` stays true after death (both enabled-awaiting and disabled branches), which is the sim guarantee the harvest depends on, but the harvest→profile→manifest-shape path is the same pre-existing Godot-coupled seam already deferred in the story-3.13 review (no headless harness). Extracting a Godot-free harvest resolver would close both. Flagged by the Verification-Gap + Intent-Alignment review layers.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-hero-harvest-resolver-extraction

## Deferred from: follow-up code review of story-3.14 (2026-07-08)

### DW-33: `CommandCardSystem.RefreshReviveButtons` filters awaiting heroes by `Alive`+`AwaitingRevival`+`OwnerFaction` but NOT by `SourceDef != null`, so a hypothetical live hero minted without a `SourceDef` would render a priced, affordable revive button that silently no-ops — `BuildingSystem.ReviveHeroCommand` rejects the order at its `SourceDef == null` guard (returning false, no spend) but pushes no `OrderDenied` cue, leaving a dead button and a hero stuck awaiting forever.
origin: migrated from legacy ledger ("Deferred from: follow-up code review of story-3.14 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-14-hero-death-revival.md`
location: n/a
reason: summary: `CommandCardSystem.RefreshReviveButtons` filters awaiting heroes by `Alive`+`AwaitingRevival`+`OwnerFaction` but NOT by `SourceDef != null`, so a hypothetical live hero minted without a `SourceDef` would render a priced, affordable revive button that silently no-ops — `BuildingSystem.ReviveHeroCommand` rejects the order at its `SourceDef == null` guard (returning false, no spend) but pushes no `OrderDenied` cue, leaving a dead button and a hero stuck awaiting forever. evidence: Sim-side guards already prevent any resource loss (`ReviveHeroCommand` returns false with no debit when `SourceDef == null`, and `RespawnHero` re-checks it), and every PRODUCTION mint path now threads a def (`HeroProfileLoader`/`ScenarioApplier` → widened `HeroStore.Mint`), so this is only reachable if a future mint path (e.g. a persistence-restore rail) creates a live-ticked hero with a null `SourceDef`. Presentation-only and headless-unverifiable in this environment (`CommandCardSystem` is a Godot `Node`). Cheap closures if it ever becomes reachable: filter `SourceDef != null` in `RefreshReviveButtons`, and/or have `HandleHeroDeath` treat a null-`SourceDef` hero as off-field (not awaiting). Flagged by the Blind Hunter review layer (follow-up finding #2).
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Unreachable today: every production mint path threads a SourceDef and the sim guards (ReviveHeroCommand/RespawnHero null-SourceDef checks) prevent resource loss, so no live hero with a null SourceDef can render a revive button. A cheap defensive filter can be added only if a future null-SourceDef mint path (e.g. a persistence-restore rail) is introduced.

## Deferred from: code review of story-3.15 (2026-07-08)

### DW-34: `ItemSystem.ApplyStatModifierIfAny` ignores the return value of `ModifierStore.Apply`, so a hero already at `MaxModifiersPerEntity` (growth + passives + several carried items) that picks up another stat item claims the item and fills the slot but gains no stat bonus and no denial cue — a silently inert item.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `ItemSystem.ApplyStatModifierIfAny` ignores the return value of `ModifierStore.Apply`, so a hero already at `MaxModifiersPerEntity` (growth + passives + several carried items) that picks up another stat item claims the item and fills the slot but gains no stat bonus and no denial cue — a silently inert item. evidence: `_modifiers.Apply(...)` returns a success/slot indicator that is discarded at the pickup site; on drop, `RemoveByModifierId` finds nothing (consistent). Deterministic across peers (same cap, same order) so no desync and no resource loss, but the item is silently useless. Degenerate — requires a hero at the per-entity modifier cap. Fix options: check the `Apply` result and deny the pickup (keep the item on the ground) or push a feedback cue. Flagged by the Edge Case review layer (#3).
status: done 2026-07-27
resolution: resolved by sweep bundle dw-item-pickup-robustness

### DW-35: `EntityPlacer.PlaceItem`'s undo/redo captures the packed item ref from the original `Create`, so place→undo→redo→undo leaks the redone ground item (redo's new ref is discarded; undo resolves the now-dead original ref and `TryResolveRef` fails to destroy the live instance).
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `EntityPlacer.PlaceItem`'s undo/redo captures the packed item ref from the original `Create`, so place→undo→redo→undo leaks the redone ground item (redo's new ref is discarded; undo resolves the now-dead original ref and `TryResolveRef` fails to destroy the live instance). evidence: Editor-only (`EntityPlacer` is a Godot `Node`, headless-unverifiable); no sim/determinism impact. Items are ref-generation-stamped so the stale-ref mismatch bites here where other placement modes tolerate it. Cheap closure: capture the new ref inside the redo closure. Flagged by the Edge Case review layer (#5).
status: open

### DW-36: A charged consumable's effect graph executes at order-apply time (inside `OrderApplier.Apply` → `ItemSystem.UseItemCommand`), not at the index-9 `ItemSystem.Tick` position, so a future RNG-drawing consumable would draw from the shared `world.Rng` at a different interleave point than the documented system order.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: A charged consumable's effect graph executes at order-apply time (inside `OrderApplier.Apply` → `ItemSystem.UseItemCommand`), not at the index-9 `ItemSystem.Tick` position, so a future RNG-drawing consumable would draw from the shared `world.Rng` at a different interleave point than the documented system order. evidence: The only shipped consumable is a deterministic self-heal (no RNG), and the spec's Block-If forbids a random effect leaf until the reserved `SimRng` random-selection enforcement lands, so this is latent today. When a random consumable ships it should adopt the `AbilityCastSystem` deferred-intent pattern (execute in the system tick, not at dispatch) for online/replay RNG parity (Epic 9). Flagged by the Edge Case + Verification-Gap review layers.
status: open
decision: 2026-07-28 correct-course — keep open, latent; trigger = a RNG-drawing consumable shipping

### DW-37: `ScenarioApplier` creates placed items in `ScenarioData.Items` array order (packed refs 0,1,2… follow array order) while `StartStateHash` canonicalizes item order (sorts by item_id/X/Z) before folding, so two scenarios with the same item set in different array order hash identically yet assign different runtime refs — a `PickupItem`/inventory ref could resolve to a different physical item per peer.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `ScenarioApplier` creates placed items in `ScenarioData.Items` array order (packed refs 0,1,2… follow array order) while `StartStateHash` canonicalizes item order (sorts by item_id/X/Z) before folding, so two scenarios with the same item set in different array order hash identically yet assign different runtime refs — a `PickupItem`/inventory ref could resolve to a different physical item per peer. evidence: Only triggers when peers load differently-ordered-but-same-set item arrays (tampered/divergent files); byte-identical files assign identical refs. This is a pre-existing architectural property shared with unit/building placement (entity ids also follow array order while `CanonicalModelHash` sorts them). Closure: canonicalize placement order in `ScenarioApplier` (sort before `Create`) for items and, ideally, the pre-existing unit/building loops too, or fold array order into the hash. Flagged by the Blind Hunter review layer (F2).
status: open
decision: 2026-07-25 Canonicalize placement order — Sort ScenarioApplier's Items (and ideally the unit/building loops) by the same canonical key before Create; golden re-baseline
decision: 2026-07-25 Canonicalize placement order — Sort ScenarioApplier's Items (and ideally the unit/building loops) by the same canonical key before Create; golden re-baseline
decision: 2026-07-19 Canonicalize placement order — Sort ScenarioApplier's Items (and the unit/building loops) by the same canonical key before Create so runtime refs match the hash order. Requires a golden re-baseline.

### DW-38: `ItemDefinitionValidator` permits a charged consumable (`charges > 0`) to also carry non-zero stat deltas, crossing the `ItemDefinition`/validator docs' asserted stat-item-XOR-consumable archetype split; the crossing is untested.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `ItemDefinitionValidator` permits a charged consumable (`charges > 0`) to also carry non-zero stat deltas, crossing the `ItemDefinition`/validator docs' asserted stat-item-XOR-consumable archetype split; the crossing is untested. evidence: The validator checks `charges>0 ⇒ effect present` and `charges==0 ⇒ no effect`, but never `charges>0 ⇒ no stat deltas`. The runtime handles a hybrid consistently (applies the carried modifier, removes it on consume-to-zero), so this is a design/doc decision (allow hybrid buff-consumables like some WC3 items, or enforce the XOR) rather than a bug. Closure: either add the XOR rule + a reject oracle, or soften the doc comments to permit hybrids and add coverage. Flagged by the Blind Hunter review layer (F6).
status: done 2026-07-27
resolution: resolved by sweep bundle dw-item-definition-validator-hardening
decision: 2026-07-25 Permit hybrids + document — Soften the ItemDefinition/validator docstrings to allow WC3-style buff-consumables and add hybrid apply/consume coverage
decision: 2026-07-25 Permit hybrids + document — Soften the ItemDefinition/validator docstrings to allow WC3-style buff-consumables and add hybrid apply/consume coverage
decision: 2026-07-08 Permit hybrids + document — Soften the ItemDefinition/validator docstrings to allow buff-consumables (WC3-style: a charged item that also grants a passive stat bonus) and add coverage for the hybrid apply/consume path. Least restrictive for creators; matches what the runtime already does.

### DW-39: The pickup move-to (traversal) path has no automated coverage — every `ItemSystemTests` case and the item golden spawn the item on top of the hero, so only the immediate-proximity claim is exercised; the `sqrDist > range → write MoveTarget → return` steering branch is untested.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: The pickup move-to (traversal) path has no automated coverage — every `ItemSystemTests` case and the item golden spawn the item on top of the hero, so only the immediate-proximity claim is exercised; the `sqrDist > range → write MoveTarget → return` steering branch is untested. evidence: Claim resolution, full-inventory denial, two-hero race, use/drop/death are all covered, but a regression in the steering branch (wrong MoveTarget, never reaching claim range) would ship green. Also relatedly: pickup steers via a direct `world.MoveTarget` write (not FlowFieldBridge) with no order timeout, so a hero ordered onto an unreachable item steers into an obstacle indefinitely (gameplay, not determinism). Closure: a test that spawns an item away from the hero and asserts it navigates then claims; consider routing pickup steering through the flow-field path and adding an unreachable-item timeout. Flagged by the Intent-Alignment (B) + Blind Hunter (F11) review layers.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-item-pickup-robustness

### DW-40: Manual `DropItem` has no player-facing UI trigger and no replay/golden coverage — `SelectionSystem` wires right-click→pickup and `T`→use but nothing issues a `DropItem` order, so in-game drop is reachable only via hero death; the AC4 replay/golden covers drop only as death-drop, not a manual `DropItem` order.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: Manual `DropItem` has no player-facing UI trigger and no replay/golden coverage — `SelectionSystem` wires right-click→pickup and `T`→use but nothing issues a `DropItem` order, so in-game drop is reachable only via hero death; the AC4 replay/golden covers drop only as death-drop, not a manual `DropItem` order. evidence: The `DropItem` sim command + `NetworkCommand` dispatch + the drop primitive are implemented and unit-tested (`DropStatItem_RemovesModifier_AndReturnsToGround`), but the player-facing drop affordance and the full inventory-grid UI are Story 3.16. Closure: 3.16 adds the inventory-grid drop button + a manual-drop replay case. Flagged by the Intent-Alignment review layer (C).
status: done 2026-07-08
resolution: already resolved: Story 3.16 (done) added the player-facing DropItem UI trigger: per-slot Drop buttons (CommandCardSystem.cs:827 → OnInventoryDropPressed:649 → SelectionSystem.IssueDropItemCommand:242, lockstep-enqueued), and manual DropItem replay coverage exists (CommandApplyParityTests.cs:408 ReplayVsLive_UseAndDropItem_ApplyIdentically + ItemSystemTests.cs:142). The 'no UI trigger / no coverage' gap is closed.

### DW-41: The shipped use-hotkey hard-codes inventory slot 0 (`IssueUseItemCommand(hero, 0)`) so the golden's slot-1 use is unreachable from the UI, and the `StartStateHash` placed-item byte layout (`MixStr(item_id)` + X/Z Raw, sorted) is not independently pinned — `PlacedItem_ChangesHash` only asserts inequality and the independent-FNV test folds only the inventory empties.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: The shipped use-hotkey hard-codes inventory slot 0 (`IssueUseItemCommand(hero, 0)`) so the golden's slot-1 use is unreachable from the UI, and the `StartStateHash` placed-item byte layout (`MixStr(item_id)` + X/Z Raw, sorted) is not independently pinned — `PlacedItem_ChangesHash` only asserts inequality and the independent-FNV test folds only the inventory empties. evidence: Both are low-consequence: the general slot-selecting use UI is Story 3.16 (the command surface accepts any slot and is tested at slots 0/1/2), and `StartStateHash` is not yet consumed in production (handshake wiring is Epic 9). Closure: 3.16 ships the inventory-grid use with per-slot selection; a follow-up extends the independent-FNV recomputation to the placed-item fold. Flagged by the Intent-Alignment (D) + Verification-Gap review layers.
status: done 2026-07-08
resolution: already resolved: Both facets closed by Story 3.16 (done): per-slot inventory use ships (CommandCardSystem.cs:643 OnInventoryUsePressed → SetSelectedInventorySlot + IssueUseItemCommand(hero, slot); T-hotkey reads the selected slot, SelectionSystem.cs:497), and the placed-item StartStateHash byte layout is independently pinned (StartStateHashTests.cs:182 PlacedItem_ChangesHash + the anti-tautology independent-FNV rebuild at :57-89).

## Deferred from: follow-up review of story-3.15 (2026-07-08)

_A second, independent review pass (four Opus-4.8 layers) over the same `04022cc..HEAD` diff. No intent_gap/bad_spec; 3 patches applied in-pass (UseItemCommand liveness guard, reset-keystone item teeth, a stale comment). These 4 are genuinely-new deferrals surfaced by this pass._

### DW-42: `ItemDefinitionValidator` applies one uniform `MAX_ITEM_STAT_DELTA = ±1000` magnitude cap to all four stat deltas, including `move_speed_delta`, but base hero speeds are single-digit — so a validated item with `move_speed_delta = 1000` yields effective speed ≈1003 (a hero moving ~1000 world-units/tick, tunnelling through pathing/obstacles), and `move_speed_delta = -1000` clamps the hero to 0 (permanently frozen).
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `ItemDefinitionValidator` applies one uniform `MAX_ITEM_STAT_DELTA = ±1000` magnitude cap to all four stat deltas, including `move_speed_delta`, but base hero speeds are single-digit — so a validated item with `move_speed_delta = 1000` yields effective speed ≈1003 (a hero moving ~1000 world-units/tick, tunnelling through pathing/obstacles), and `move_speed_delta = -1000` clamps the hero to 0 (permanently frozen). evidence: `ModifierSystem` floors effective stats at zero (`Fixed.Max(Fixed.Zero, …)`) but has NO top clamp, and the ±1000 cap's own doc only claims it prevents Fixed 16.16 negative-wrap — it does nothing for the speed-scale mismatch. Deterministic (every peer computes the same broken speed) so no desync, but game-breaking with authored content the validator green-lights. No shipped item stresses it (`ring_of_vigor` has `move_speed_delta = 0`). Closure: a per-stat cap (a much tighter `move_speed_delta` bound) or a top clamp in the effective-speed recompute — the latter folds with the pre-existing 2.2a `ModifierSystem` "no upper clamp" deferral. Flagged by the Blind Hunter review layer (F2).
status: done 2026-07-27
resolution: resolved by sweep bundle dw-item-definition-validator-hardening

### DW-43: `ItemSystem.OnEntityDestroyed` early-returns (dropping nothing) if `_heroes.TryResolveRef(HeroIndex[entityId], …)` fails, so a PERMANENT (non-revivable) hero removal that tears down the `HeroStore` row BEFORE the `EntityWorld` entity would orphan the carried items (left `Held = true` with a dead `CarrierHeroSlot`, unreachable — a later `PickupItem` sees `Held` and voids).
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `ItemSystem.OnEntityDestroyed` early-returns (dropping nothing) if `_heroes.TryResolveRef(HeroIndex[entityId], …)` fails, so a PERMANENT (non-revivable) hero removal that tears down the `HeroStore` row BEFORE the `EntityWorld` entity would orphan the carried items (left `Held = true` with a dead `CarrierHeroSlot`, unreachable — a later `PickupItem` sees `Held` and voids). evidence: The death-drop's correctness silently depends on an un-asserted teardown order (entity destroyed while the hero row is still Alive). Every current path keeps the row for revival (the tests only exercise `world.Destroy(e)` / lethal `DamageResolver` with the row alive), so this is latent — not reachable until a permanent hero-removal path exists. Closure: when permanent hero removal lands, assert/guarantee the entity `Destroy` (and its `OnDestroy` drop) precedes the `HeroStore.Destroy(slot)`, or drop items keyed off the entity independently of the row resolve. Flagged by the Blind Hunter review layer (F4).
status: open
decision: 2026-07-28 correct-course — keep open, latent; trigger = a permanent (non-revivable) hero-removal path

### DW-44: In `SelectionSystem`, a right-click within `PICK_RADIUS` of a ground item issues `PickupItem` to only the first hero in a mixed selection and `return`s, so the other N selected units receive no move order at all (and it takes priority over an attack-move onto a nearby enemy) — "my army randomly stops moving near an item."
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: In `SelectionSystem`, a right-click within `PICK_RADIUS` of a ground item issues `PickupItem` to only the first hero in a mixed selection and `return`s, so the other N selected units receive no move order at all (and it takes priority over an attack-move onto a nearby enemy) — "my army randomly stops moving near an item." evidence: `if (groundItem >= 0) { IssuePickupCommand(heroForPickup, groundItem); return; }` short-circuits the normal move/attack-move dispatch for the whole selection. Presentation-only (no sim/determinism impact), but a real UX regression. Closure (3.16 UI pass): issue the pickup to the nearest eligible hero AND still route the remaining selection through the normal move/attack-move path, or gate the pickup shortcut on a single-hero selection. Flagged by the Blind Hunter review layer (F7).
status: done 2026-07-08
resolution: already resolved: Resolved: the SelectionSystem right-click pickup branch no longer strands the rest of a mixed selection — only the pickup hero is excluded and the remaining units fall through to the normal move/attack dispatch (SelectionSystem.cs:437-444).

### DW-45: `EntityPlacer.PlaceItem` reads `_itemIndex % _itemRegistry.Count` but `_itemIndex` is never incremented anywhere, so the editor's Item palette can only ever place registry item 0 — its own field comment ("cycled by re-clicking the Item mode") describes cycling that is not implemented.
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.15 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md`
location: n/a
reason: summary: `EntityPlacer.PlaceItem` reads `_itemIndex % _itemRegistry.Count` but `_itemIndex` is never incremented anywhere, so the editor's Item palette can only ever place registry item 0 — its own field comment ("cycled by re-clicking the Item mode") describes cycling that is not implemented. evidence: Editor-only (Godot `Node`, headless-unverifiable, no sim/determinism impact); scenarios seed varied items via JSON through `ScenarioApplier` (tests use that path), so only the interactive palette is limited. Closure: increment `_itemIndex` on Item-mode (re-)selection — folds naturally into the Story 3.16 full item-authoring/placement UI. Flagged by the Edge Case review layer (#4).
status: done 2026-07-08
resolution: already resolved: Resolved: EntityPlacer's _itemIndex is now advanced/cycled on Item-mode reselection (EntityPlacer.cs:951-967, `_itemIndex = capturedIdx`), so the editor Item palette can place items beyond registry index 0.

## Deferred from: story-3.16 review (2026-07-08)

### DW-46: The `UnitCommand.UseItem = 16` / `DropItem = 17` doc comments in `godot/src/Core/EntityWorld.cs` say "Handled by OrderApplier BEFORE the entity guard", but `NetworkCommand.cs` correctly dispatches them AFTER the `IsAlive`/`FactionOf` ownership guard (the deliberate 3.15 anti-cheat fix).
origin: migrated from legacy ledger ("Deferred from: story-3.16 review (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-16-item-authoring-shop-buildings-inventory-ui.md`
location: godot/src/Core/EntityWorld.cs
reason: summary: The `UnitCommand.UseItem = 16` / `DropItem = 17` doc comments in `godot/src/Core/EntityWorld.cs` say "Handled by OrderApplier BEFORE the entity guard", but `NetworkCommand.cs` correctly dispatches them AFTER the `IsAlive`/`FactionOf` ownership guard (the deliberate 3.15 anti-cheat fix). evidence: Pre-existing stale comment inherited from Story 3.15 (Story 3.16 did not touch those enum lines). Behavior is the safe one; the comment is wrong and is a latent anti-cheat trap — a future edit trusting the comment could move the dispatch before the guard, letting a player force an enemy hero to drop/use items. One-line doc fix. Flagged by the Blind Hunter review layer (F7).
status: open

## Deferred from: follow-up review of story-3.16 (2026-07-08)

_A second, independent review pass (four Opus-4.8 layers: Blind Hunter, Edge Case Hunter, Verification-Gap, Intent-Alignment) over the same `9ceacdb..HEAD` diff. No intent_gap/bad_spec; 3 patches applied in-pass (crystal-cost buy atomicity test, under-construction shop reject test, editor spinner ranges clamped to the validator caps). These 4 are genuinely-new deferrals this pass surfaced — recorded as NEW entries only._

### DW-47: The item editor's `Id` text field is written to `_current.Id` verbatim (`ItemCardPanel.Edit.cs`) with no filename-safe/charset validation — `SanitizeId` runs only in `UniqueId` for New/Duplicate, and both `ItemDefinitionValidator.Validate` and `ValidateFields` reject only `IsNullOrEmpty(id)`. `Persist()` then does `Path.Combine(itemsDir, id + ".json")` + `File.WriteAllText`/`File.Move`/`File.Delete`, so an Id like `../../foo` resolves outside the items directory.
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.16 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-16-item-authoring-shop-buildings-inventory-ui.md`
location: ItemCardPanel.Edit.cs
reason: summary: The item editor's `Id` text field is written to `_current.Id` verbatim (`ItemCardPanel.Edit.cs`) with no filename-safe/charset validation — `SanitizeId` runs only in `UniqueId` for New/Duplicate, and both `ItemDefinitionValidator.Validate` and `ValidateFields` reject only `IsNullOrEmpty(id)`. `Persist()` then does `Path.Combine(itemsDir, id + ".json")` + `File.WriteAllText`/`File.Move`/`File.Delete`, so an Id like `../../foo` resolves outside the items directory. evidence: Real path-traversal footgun, but low-consequence: a local single-developer authoring tool where the "input" is the creator's own typed Id, and it mirrors the pre-existing `UnitCardPanel` convention (same unguarded Id field, sanitized only on New/Duplicate) that Story 3.16 was told to mirror. Closure: add a filename-safe charset check to `ItemDefinitionValidator` (fail-closed, located field message — same shape as the new missing-icon reject) and apply the same guard to `UnitCardPanel` so the convention is fixed once. Flagged by the Edge Case review layer (#1).
status: done 2026-07-27
resolution: resolved by sweep bundle dw-item-definition-validator-hardening

### DW-48: `ProfileInventoryItem.Slot` defaults to `-1` in its C# constructor, but System.Text.Json passes `default(int)=0` (NOT the `-1` default) for a missing `"slot"` key — confirmed empirically: `JsonSerializer.Deserialize<ProfileInventoryItem>("{\"item_id\":\"ring\",\"charges\":2}").Slot == 0`, and `LocalProfileSource.JsonOptions` adds no special handling. So a slot-less "legacy" inventory profile would deserialize every item to `Slot=0`, defeating the documented `Slot<0` contiguous-fallback in `HeroProfileLoader.ReMintInventory` and collapsing a multi-item loadout onto slot 0 (duplicate-slot skip drops all but the first).
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.16 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-16-item-authoring-shop-buildings-inventory-ui.md`
location: n/a
reason: summary: `ProfileInventoryItem.Slot` defaults to `-1` in its C# constructor, but System.Text.Json passes `default(int)=0` (NOT the `-1` default) for a missing `"slot"` key — confirmed empirically: `JsonSerializer.Deserialize<ProfileInventoryItem>("{\"item_id\":\"ring\",\"charges\":2}").Slot == 0`, and `LocalProfileSource.JsonOptions` adds no special handling. So a slot-less "legacy" inventory profile would deserialize every item to `Slot=0`, defeating the documented `Slot<0` contiguous-fallback in `HeroProfileLoader.ReMintInventory` and collapsing a multi-item loadout onto slot 0 (duplicate-slot skip drops all but the first). evidence: Confirmed real by an exploratory test (removed — it cannot assert the documented `-1` without a code fix, and asserting `0` would enshrine the bug). Defensive-only today: hero-inventory persistence is NEW in Story 3.16 (all real captures set `Slot` explicitly via `CaptureInventory`), so no slot-less profile data exists — the `-1` fallback branch is reachable only through the internal 2-arg `new(id, charges)` ctor (tests) and future hand-edited JSON. A reliable fix (nullable-backed `int? Slot` mapping absent→-1, or a small converter) ripples through `CaptureInventory`/`ReMintInventory` and the persistence tests, exceeding safe unattended scope. Closure: make missing-slot JSON map to `-1` (nullable-backed property or converter) and pin it with the round-trip test. Flagged by the Blind Hunter review layer (#7).
status: done 2026-07-19
resolution: resolved by sweep bundle dw-hero-profile-load-hardening

### DW-49: The shop Buy button (`CommandCardSystem.RefreshShopButtons`) is enabled on affordability + "an owned hero is in range" only; it never consults the resolved buyer's free-slot state, and `FindNearestOwnedHero` always targets the single NEAREST owned hero. If the nearest in-range hero has a full inventory but a farther in-range owned hero has room, the Buy button lights and priced, yet `BuyItemCommand`'s free-slot guard rejects (OrderDenied) — an affordable, in-range Buy that silently does nothing.
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.16 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-16-item-authoring-shop-buildings-inventory-ui.md`
location: n/a
reason: summary: The shop Buy button (`CommandCardSystem.RefreshShopButtons`) is enabled on affordability + "an owned hero is in range" only; it never consults the resolved buyer's free-slot state, and `FindNearestOwnedHero` always targets the single NEAREST owned hero. If the nearest in-range hero has a full inventory but a farther in-range owned hero has room, the Buy button lights and priced, yet `BuyItemCommand`'s free-slot guard rejects (OrderDenied) — an affordable, in-range Buy that silently does nothing. evidence: The sim is correct (deterministic reject, zero resource loss); this is a presentation buyer-selection defect with a narrow trigger (2+ owned heroes at one shop, nearest full). Low consequence, no state corruption. The good fix (prefer the nearest in-range owned hero WITH a free slot) is UI logic that needs a live `/godot-verify` session to implement and confirm safely — out of scope for this headless unattended pass. Closure: fold into the story's prescribed `/godot-verify` HUD pass. Flagged by the Blind Hunter (#1) and Edge Case (#3) review layers.
status: open
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 10.9 (Epic 10 live-verify batch, A5-E9)

### DW-50: Icon TEXTURES are not rendered anywhere — the in-match 6-slot inventory grid, the shop Buy buttons, and the hero-picker slot card all identify items by name + `x{charges}` text + tooltip only. `CommandCardSystem` sets `Button.Text` and contains no `Texture`/`TextureRect`/`Button.Icon`, despite `ItemDefinition.Icon` being authored and validated (missing-file reject). AC4 reads "a 6-slot inventory grid shows carried items with icons".
origin: migrated from legacy ledger ("Deferred from: follow-up review of story-3.16 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-16-item-authoring-shop-buildings-inventory-ui.md`
location: n/a
reason: summary: Icon TEXTURES are not rendered anywhere — the in-match 6-slot inventory grid, the shop Buy buttons, and the hero-picker slot card all identify items by name + `x{charges}` text + tooltip only. `CommandCardSystem` sets `Button.Text` and contains no `Texture`/`TextureRect`/`Button.Icon`, despite `ItemDefinition.Icon` being authored and validated (missing-file reject). AC4 reads "a 6-slot inventory grid shows carried items with icons". evidence: The raw-Godot HUD matches the host file's established train/revive/worker/ability button pattern (all text buttons — the "reuse established patterns" constraint), so items are fully identifiable, but the explicit "with icons" acceptance surface is met as text, not graphics. Presentation-only; needs a live `/godot-verify` session to add texture loading (`ItemDefinition.Icon` res:// path → `Button.Icon`) across the three surfaces and confirm it renders — out of scope for this headless pass. Closure: fold icon rendering into the story's prescribed `/godot-verify` HUD/editor pass. Flagged by the Intent-Alignment review layer.
status: open
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 10.9 (Epic 10 live-verify batch, A5-E9)

## Deferred from: code review of story-3.17 (2026-07-08)

### DW-51: Editor delete→undo of a hero-linked unit drops its `HeroIndex` (the packed `HeroStore` handle): `SnapshotUnit`/`RestoreUnit` never capture or restore it, and `ApplyUnitDefinition` deliberately does not set it (hero links are runtime-minted, not def-derived), so a restored hero comes back as a plain unit. AC1 lists "hero fields" among the authored state that must survive.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.17 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-17-editor-undo-restoreunit-fidelity-widen-unitsnapshot.md`
location: n/a
reason: summary: Editor delete→undo of a hero-linked unit drops its `HeroIndex` (the packed `HeroStore` handle): `SnapshotUnit`/`RestoreUnit` never capture or restore it, and `ApplyUnitDefinition` deliberately does not set it (hero links are runtime-minted, not def-derived), so a restored hero comes back as a plain unit. AC1 lists "hero fields" among the authored state that must survive. evidence: Reachable — the editor place path never mints heroes, but `MainScene.ResetToAuthoredStart` re-mints heroes into the Edit-mode world on the Edit↔Play round-trip, and `DeleteUnit`/undo are Edit-mode-only, so a `HeroIndex`-set unit is deletable. Not part of the chartered def-derived drop-debt (the six deferred entries this story closes are all def-derived fields; `HeroIndex` is runtime state). Restoring it correctly is design-laden: `EntityWorld.Destroy` does not free the `HeroStore` row, the packed handle is generation-stamped (ABA), and the epic principle is "authored state / never a mid-game snapshot / playtest XP discarded" — so whether editor-undo should resurrect a runtime hero link at all is a human design decision, not a mechanical field-add. Flagged by the Edge Case Hunter and Intent-Alignment review layers.
status: done 2026-07-08
resolution: closed by human decision: Accept that editor-undo of a hero unit returns a plain unit and document it as consistent with 'runtime hero state is not authored state' — hero links are re-minted at play, so the loss is invisible in normal use.
decision: 2026-07-08 Restore as a plain unit (document) — Accept that editor-undo of a hero unit returns a plain unit and document it as consistent with 'runtime hero state is not authored state' — hero links are re-minted at play, so the loss is invisible in normal use.

### DW-52: Deleting a hero-linked unit in the editor orphans its `HeroStore` row — `EntityWorld.Destroy` does not free the row nor clear the store's back-reference `EntityId`, so the row leaks and its `EntityId` dangles at a dead/recycled entity.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.17 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-17-editor-undo-restoreunit-fidelity-widen-unitsnapshot.md`
location: n/a
reason: summary: Deleting a hero-linked unit in the editor orphans its `HeroStore` row — `EntityWorld.Destroy` does not free the row nor clear the store's back-reference `EntityId`, so the row leaks and its `EntityId` dangles at a dead/recycled entity. evidence: Pre-existing (`Destroy` never touched `HeroStore`; Story 3.17 did not change `Destroy`), surfaced incidentally by the hero-fields review. Inert while no editor flow deletes a re-minted hero, but couples with the hero-link-drop entry above — both want a defined lifecycle for "a hero unit is removed in the editor." Flagged by the Edge Case Hunter review layer.
status: open
decision: 2026-07-27 Free the HeroStore row on editor delete — In the editor delete path, free the HeroStore row and clear its EntityId back-reference (pairs with DW-51 'restore as plain unit').
decision: 2026-07-08 Free the row on delete — In the editor delete path, free the HeroStore row and clear its EntityId back-reference (assumes DW-51 resolves to 'restore as plain unit'). Pairs with DW-51 option 2.
decision: 2026-07-28 correct-course — bundle hero-row-free-on-editor-delete (Epic 15, Story 15.8)

### DW-53: Editor delete→undo restores a unit at full HP and full Energy — `SnapshotUnit` captures `BaseMaxHealth` (fed to `Create`, which sets `Health = MaxHealth`) and no current `Health`, and the def path re-derives `Energy = MaxEnergy`, so a damaged / energy-spent unit round-trips to full.
origin: migrated from legacy ledger ("Deferred from: code review of story-3.17 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-17-editor-undo-restoreunit-fidelity-widen-unitsnapshot.md`
location: n/a
reason: summary: Editor delete→undo restores a unit at full HP and full Energy — `SnapshotUnit` captures `BaseMaxHealth` (fed to `Create`, which sets `Health = MaxHealth`) and no current `Health`, and the def path re-derives `Energy = MaxEnergy`, so a damaged / energy-spent unit round-trips to full. evidence: Matches the pre-existing `EntityPlacer.RestoreUnit` behavior (not a regression) and is inert for editor-placed units (always full HP/energy, no combat in Edit mode). Only observable if delete→undo runs on a unit that took damage or spent energy during a playtest — a deliberate "should editor undo preserve current HP/energy?" decision worth making when the Edit↔Play loop's runtime-state semantics are revisited. Flagged by the Blind Hunter, Edge Case Hunter, and Verification-Gap review layers.
status: done 2026-07-08
resolution: closed by human decision: Accept the full restore — it is inert for editor-placed units and consistent with 'playtest runtime state is discarded'; no change needed.
decision: 2026-07-08 Accept full-HP/Energy restore — Accept the full restore — it is inert for editor-placed units and consistent with 'playtest runtime state is discarded'; no change needed.

### DW-54: `UnitSnapshot` pins the source `UnitDefinition` by reference for the entire undo-history lifetime and `RestoreUnit` re-applies it assuming its `AbilityIndices` are still resolved; nothing guarantees the def object stays alive/unmutated/resolved across a long editor session (e.g. an in-place reload/replace of the def).
origin: migrated from legacy ledger ("Deferred from: code review of story-3.17 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-3-17-editor-undo-restoreunit-fidelity-widen-unitsnapshot.md`
location: n/a
reason: summary: `UnitSnapshot` pins the source `UnitDefinition` by reference for the entire undo-history lifetime and `RestoreUnit` re-applies it assuming its `AbilityIndices` are still resolved; nothing guarantees the def object stays alive/unmutated/resolved across a long editor session (e.g. an in-place reload/replace of the def). evidence: Theoretical today (defs are long-lived, resolved once at scenario link, and not mutated in place), but if a future editor feature reloads or edits a def in place, an undo could re-derive from a changed/unresolved def or silently drop abilities. Low likelihood; no current trigger. Flagged by the Blind Hunter review layer.
status: open
decision: 2026-07-28 correct-course — keep open, latent; trigger = an editor feature that reloads/edits a UnitDefinition in place

## Deferred from: code review of story-4.1 (2026-07-08)

### DW-55: `BuildingDefinitionValidator` cannot distinguish an omitted `hp` (silently defaults to 100f via the inherited, non-nullable `UnitDefinition.Hp`) from an intentionally-authored `100`, so a creator who forgets `hp` gets a silently-wrong-HP building instead of a located import error — unlike `construction_time`/`supply_bonus`/`produces_category`, which are all required-nullable and do catch omission.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.1 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-1-data-drive-the-building-definition-runtime-building-store.md`
location: n/a
reason: summary: `BuildingDefinitionValidator` cannot distinguish an omitted `hp` (silently defaults to 100f via the inherited, non-nullable `UnitDefinition.Hp`) from an intentionally-authored `100`, so a creator who forgets `hp` gets a silently-wrong-HP building instead of a located import error — unlike `construction_time`/`supply_bonus`/`produces_category`, which are all required-nullable and do catch omission. evidence: Real and caused by this story — `Hp` went from vestigial (ignored by `BuildingStore.Create`'s switch) to load-bearing (threaded verbatim into `Health`/`MaxHealth` once a def resolves) in this diff, but no creator-facing building-authoring UI exists yet (Story 4.5 not shipped), so there is no reachable path today; both shipped faction files author correct `hp` values. Closing it properly requires either a `UnitDefinition`-wide nullable-`Hp` change (blast radius beyond buildings, touches unit spawning/validation too) or JSON-presence tracking (new machinery) — disproportionate for this story. A narrower `Hp <= 0` reject was added during review (catches typo'd zero/negative), but not full omission. Flagged independently by the Blind Hunter and Verification-Gap review layers; revisit alongside Story 4.5's own required-field validation UX.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening
decision: 2026-07-17 JSON-presence tracking — Detect an omitted hp via JSON-presence tracking (or a buildings-only nullable Hp) and emit a located error, avoiding the UnitDefinition-wide change.
decision: 2026-07-16 JSON-presence tracking — Detect an omitted hp via JSON-presence tracking (or a buildings-only nullable Hp) and emit a located error, avoiding the UnitDefinition-wide change.

## Deferred from: code review of story-4.2 (2026-07-08)

### DW-56: `TechTreeValidator.Validate` silently excludes any `Buildings[]` entry with a missing/empty `Id` from BOTH the referential-lint id set and the cycle-detection graph, so a malformed building entry can sail through this story's new import-time checks entirely unvalidated.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.2 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-2-data-driven-tech-prerequisite-resolution-with-import-time-cycle-referential-lint.md`
location: n/a
reason: summary: `TechTreeValidator.Validate` silently excludes any `Buildings[]` entry with a missing/empty `Id` from BOTH the referential-lint id set and the cycle-detection graph, so a malformed building entry can sail through this story's new import-time checks entirely unvalidated. evidence: Pre-existing gap this story merely inherits — no validator anywhere (not `BuildingDefinitionValidator`, not the new `TechTreeValidator`) rejects a building with an empty `id`, unlike `UnitDefinitionValidator`'s explicit non-empty-id check for units. Low real-world risk: an empty-id building is already broken elsewhere today (its `DefinitionId`/`GetBuilding` lookups never resolve, so it can't be placed or referenced meaningfully by anything). Closure: add a non-empty-id check to `BuildingDefinitionValidator` (the natural home, mirroring `UnitDefinitionValidator`'s rule) so `TechTreeValidator` never has to special-case it. Flagged by the Blind Hunter review layer.
status: done 2026-07-16
resolution: already resolved: BuildingDefinitionValidator.Validate now merges UnitDefinitionValidator kinded 'building' (Definitions/BuildingDefinitionValidator.cs:102-105), which rejects an empty id (Definitions/UnitDefinitionValidator.cs:156-158 'must be a non-empty id.'). FactionValidator.Validate runs BuildingDefinitionValidator per building (Definitions/FactionValidator.cs:119-121) BEFORE TechTreeValidator, so an empty-id building now fails load.

### DW-57: `BuildingType.Custom` — the enum sentinel this story's `TechTreeChecker` generalization was explicitly built to support as a prerequisite TARGET — still can't be resolved as a prerequisite SOURCE through `BuildingSystem.GetBuildingPlacePrereq`/`EntityPlacer.PlaceBuilding`'s prereq-lookup, because both resolve the building def via `TechTreeChecker.BuildingTypeId(BuildingType.Custom)` which returns `""`, and `GetBuilding("")` never matches a real building.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.2 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-2-data-driven-tech-prerequisite-resolution-with-import-time-cycle-referential-lint.md`
location: n/a
reason: summary: `BuildingType.Custom` — the enum sentinel this story's `TechTreeChecker` generalization was explicitly built to support as a prerequisite TARGET — still can't be resolved as a prerequisite SOURCE through `BuildingSystem.GetBuildingPlacePrereq`/`EntityPlacer.PlaceBuilding`'s prereq-lookup, because both resolve the building def via `TechTreeChecker.BuildingTypeId(BuildingType.Custom)` which returns `""`, and `GetBuilding("")` never matches a real building. evidence: Pre-existing since Story 4.1 introduced `BuildingType.Custom` (this exact `BuildingTypeId(type) → GetBuilding(id)` chain in both call sites predates this story; 4.2 only added the trailing display-name-resolution wrapper around it) — consistent with 4.1's own Design Notes, which already documented that `Custom` has no end-to-end placement route through `BuildingSystem`/the editor and deferred that to Stories 4.5/4.6. Closure: naturally resolved once 4.5/4.6 give a `Custom` building a real placement path that threads its authored id through instead of the enum-keyed lookup. Flagged by the Edge Case Hunter and Verification-Gap review layers.
status: open
seen-again: 2026-07-15 (epic-6 retro audit — story 6-8 shipped the placement-path threading and closed DW-68, but did NOT touch the prereq-SOURCE lookup this entry names: `TechTreeChecker.BuildingTypeId(BuildingType.Custom)` still returns "" in `GetBuildingPlacePrereq`. Still open.)
decision: 2026-07-28 correct-course — keep open, latent; trigger = a Custom-building command-card/placement path enumerating authored ids

### DW-58: `TechTreeValidator.Validate` returns bare `string` error messages, unlike `BuildingDefinitionValidator`'s `(FieldPath, Message)` tuples — discarding any field-path structure a future consumer (e.g. an inline editor rejection) could key off to highlight the specific offending prerequisite edge.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.2 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-2-data-driven-tech-prerequisite-resolution-with-import-time-cycle-referential-lint.md`
location: n/a
reason: summary: `TechTreeValidator.Validate` returns bare `string` error messages, unlike `BuildingDefinitionValidator`'s `(FieldPath, Message)` tuples — discarding any field-path structure a future consumer (e.g. an inline editor rejection) could key off to highlight the specific offending prerequisite edge. evidence: Not a gap for this story's own consumer (`FactionDefinition.LoadFromFile` only ever wanted the message text, same as how it already discards `BuildingDefinitionValidator`'s field paths today), but Story 4.6's own acceptance criteria explicitly says its in-editor edge-drop rejection must be "consistent with the 4.2 import lint" — implying 4.6 will want to reuse this validator's cycle/referential logic for real-time UI feedback, where a structured field path would matter. Closure: revisit when Story 4.6 is implemented — retrofit `TechTreeValidator` to return located `(FieldPath, Message)` tuples if 4.6 needs them. Flagged by the Blind Hunter review layer.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — The named revisit trigger (Story 4.6) landed and did NOT need (FieldPath,Message) tuples — the in-editor edge-drop rejection reuses ValidateProposedEdge returning a single string, and the only import-lint consumer wants message text. The anticipated consumer was satisfied without the retrofit; moot.

### DW-59: `TechTreeValidator.Visit`'s cycle detection is a plain recursive DFS (one C# stack frame per graph depth); an extremely long (thousands-deep) prerequisite chain could in principle overflow the call stack during faction load.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.2 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-2-data-driven-tech-prerequisite-resolution-with-import-time-cycle-referential-lint.md`
location: n/a
reason: summary: `TechTreeValidator.Visit`'s cycle detection is a plain recursive DFS (one C# stack frame per graph depth); an extremely long (thousands-deep) prerequisite chain could in principle overflow the call stack during faction load. evidence: Theoretical at realistic RTS-faction authoring scale (both shipped factions have 5 buildings; even a very ambitious hand-authored tech tree tops out at dozens, not thousands, of sequential dependencies) — low priority, but the platform's "creator-extensible" ethos means an unbounded/malicious-scale JSON isn't structurally impossible. Closure: convert `Visit` to an explicit-stack iterative DFS if content scale ever approaches the point where this matters. Flagged by the Edge Case Hunter review layer.
status: open

## Deferred from: code review of story-4.3 (2026-07-08)

### DW-60: `EntityPlacer.DeleteBuilding` still computes a `capturedCost` local by directly indexing the legacy `BUILDING_COSTS` float array, but the value is never read anywhere in the method — dead code left behind when every other cost site in this same class was migrated to `ResolveCosts()`/`CanAffordAll`/`SpendAll`/`AddAll`.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.3 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-3-n-resource-registry-with-sparse-cost-maps-generalize-ore-crystal.md`
location: EntityPlacer.cs
reason: summary: `EntityPlacer.DeleteBuilding` still computes a `capturedCost` local by directly indexing the legacy `BUILDING_COSTS` float array, but the value is never read anywhere in the method — dead code left behind when every other cost site in this same class was migrated to `ResolveCosts()`/`CanAffordAll`/`SpendAll`/`AddAll`. evidence: Pre-existing — `DeleteBuilding` itself was not touched by this story's diff (confirmed: it's a different method than the placement/refund path this story migrated), so the dead local predates this change and is not a functional regression; it's just noise the story's own refactor pass didn't happen to clean up because it never modified that method. Closure: delete the unused `capturedCost` local the next time `EntityPlacer.cs` is touched. Flagged by the Blind Hunter review layer.
status: done 2026-07-16
resolution: already resolved: EntityPlacer.DeleteBuilding (godot/src/UI/EntityPlacer.cs:1585-1620) contains no capturedCost local at all — the dead BUILDING_COSTS-indexed local is gone; comment 1594 notes it is now 'Custom-safe — no BUILDING_COSTS[5] indexing'. The remaining capturedCost usages (lines 874-917) are in the placement/refund method where the value IS read.

### DW-61: `BuildingDefinition.ConstructionCost` (`=> ResolvedCost`) still has zero real callers anywhere in the codebase.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.3 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-3-n-resource-registry-with-sparse-cost-maps-generalize-ore-crystal.md`
location: n/a
reason: summary: `BuildingDefinition.ConstructionCost` (`=> ResolvedCost`) still has zero real callers anywhere in the codebase. evidence: Pre-existing since Story 4.1 introduced the property as a computed placeholder — this story only replaced its body (the old inline `{"ore":CostOre,"crystal":CostCrystal}` derivation) with a delegation to the new `UnitDefinition.ResolvedCost`, it did not add or remove any consumer. `BuildingSystem`/`CommandCardSystem` all call `ResolvedCost`/`GetBuildingCost` directly, never `ConstructionCost`. Closure: remove `ConstructionCost` (or give it a real caller) once a consumer is actually needed, or once Story 4.5/4.6's building editor confirms whether it wants this exact name. Flagged by the Blind Hunter and Verification-Gap review layers.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Human decided 2026-07-19 'Keep for API stability / a future external tool'; ConstructionCost still has zero production callers, consistent with the decision. Settled.
decision: 2026-07-19 Keep for API stability / a future external tool

### DW-62: `FactionDefinition.LoadFromFile` has no `try`/`catch` around `JsonSerializer.Deserialize` — a malformed JSON value for ANY field (e.g. `"cost": {"ore": null}` against the new non-nullable `Dictionary<string,int>` value type) throws a raw, unhandled `JsonException` instead of a located, aggregated error, unlike every field this story's own `ResourceCostValidator`/`TechTreeValidator`/`BuildingDefinitionValidator` gate reject with.
origin: migrated from legacy ledger ("Deferred from: code review of story-4.3 (2026-07-08)"), 2026-07-08
source_spec: `_bmad-output/implementation-artifacts/spec-4-3-n-resource-registry-with-sparse-cost-maps-generalize-ore-crystal.md`
location: n/a
reason: summary: `FactionDefinition.LoadFromFile` has no `try`/`catch` around `JsonSerializer.Deserialize` — a malformed JSON value for ANY field (e.g. `"cost": {"ore": null}` against the new non-nullable `Dictionary<string,int>` value type) throws a raw, unhandled `JsonException` instead of a located, aggregated error, unlike every field this story's own `ResourceCostValidator`/`TechTreeValidator`/`BuildingDefinitionValidator` gate reject with. evidence: Pre-existing for the entire loader — no field of any type (float, int, string, existing arrays) has ever been exception-shielded here; a malformed `cost_ore: "abc"` would throw identically today, pre-dating this story. The new `cost` map is simply the first `Dictionary`-typed authored field, so it's the first place this pre-existing gap becomes reachable through a nested/typed value rather than a top-level scalar. Closure: wrap the `Deserialize` call in a `try`/`catch (JsonException)` that folds a located parse failure into the same aggregate `errors` path, if/when malformed-JSON robustness (vs. malformed-but-well-typed content) becomes a real authoring concern. Flagged by the Edge Case Hunter review layer.
status: open
decision: 2026-07-28 correct-course — bundle faction-load-error-handling extended to faction-load-fail-closed (+DW-317 card-panel call sites; Epic 15, Story 15.6)

## Deferred from: code review of story-4.4 (2026-07-08)

### DW-63: `AiOpponentSystem`'s supply-headroom scoring (`SupplyHeadroom = SupplyCap - SupplyUsed`, thresholds `SUPPLY_CRITICAL`/`TIGHT`/`LOW`) can saturate to a meaningless deeply-negative magnitude when a scenario authors `supply.enabled:false` and `SupplyUsed` climbs unboundedly past `SupplyCap` (never blocked, since gating is disabled).
source_spec: `_bmad-output/implementation-artifacts/spec-4-4-data-driven-supply-cap-model-per-scenario.md`
location: godot/src/AI/AiOpponentSystem.cs
reason: summary: `AiOpponentSystem`'s supply-headroom scoring can saturate to a meaningless deeply-negative magnitude when a scenario authors `supply.enabled:false` and `SupplyUsed` climbs unboundedly past `SupplyCap`. evidence: Not a crash and not a new failure mode class — negative headroom was already reachable pre-Story-4.4 via a building-loss cap drop, and the AI's `&lt;=` threshold ladder already saturates gracefully at its worst tier ("critical") for any negative value, which remains directionally sensible (the AI still leans toward expanding supply) even though the magnitude is no longer meaningfully informative once gating is disabled and supply is no longer actually constraining anything. Closure: give the AI's strategic scoring explicit awareness of `SupplyGatingEnabled` (e.g. skip/deprioritize the `ExpandSupplyCap` action entirely when gating is disabled, since expanding an unenforced cap has no real benefit) if/when a scenario author actually ships a disabled-gating scenario with AI opponents. Flagged by the Blind Hunter and Edge Case Hunter review layers.
status: open

## Deferred from: code review of story-4.5 (2026-07-09)

### DW-64: `BuildingCardPanel.Edit.cs`'s `SaveFromRawPane` (and the identical pre-existing `UnitCardPanel.Edit.cs`) replaces the bound definition object wholesale (`_faction.Buildings[_index] = parsed; _current = parsed;`) without clearing or reconciling `_history` — every undo/redo closure captured against the old object becomes a dangling reference to an object no longer present in the list, so a Ctrl+Z after a raw-JSON Save silently no-ops instead of undoing.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/CreationSuite/BuildingCardPanel.Edit.cs, godot/src/CreationSuite/UnitCardPanel.Edit.cs
reason: summary: raw-JSON pane Save silently breaks prior undo entries (dangling object-identity references) instead of erroring or clearing history. evidence: Confirmed identical in `UnitCardPanel.Edit.cs`'s `SaveFromRawPane` (pre-existing since Story 3.4, unmodified by this story) — not a new defect, a faithfully-mirrored existing limitation. Requires a specific, uncommon sequence (edit fields → switch to raw-JSON pane → Save → Ctrl+Z) to observe; no data loss (the file itself is correct), only a silently-broken undo entry. Closure: clear `_history` (or filter out entries referencing the replaced object) whenever a raw-pane Save swaps the bound instance, for both editors. Flagged by the Blind Hunter review layer.
status: open

### DW-65: `BuildingCardPanel.LoadFactionFromPath` (the `/godot-verify` harness entry point, mirroring the identical pre-existing `UnitCardPanel.LoadFactionFromPath`) swaps in an entirely new `FactionDefinition`/`Buildings` list without clearing `_history`, so any undo entries from a previously loaded faction become dangling references to a discarded object graph.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/CreationSuite/BuildingCardPanel.cs, godot/src/CreationSuite/UnitCardPanel.cs
reason: summary: reloading a faction file via `LoadFactionFromPath` doesn't clear stale undo history from the previously loaded faction. evidence: Same object-identity class of issue as DW-64, and equally pre-existing/mirrored from `UnitCardPanel.cs`. Only reachable via the manual verify/reload path, not normal play — low real-world frequency. Closure: clear `_history` in `LoadFactionFromPath` for both editors, alongside DW-64's fix. Flagged by the Blind Hunter review layer.
status: open
decision: 2026-07-28 correct-course — co-locate with faction-load-fail-closed (same method as the DW-317 call site)

### DW-66: `BuildingCardPanel`'s `_originalId` field (and the identical pre-existing `UnitCardPanel._originalId`) is captured at Bind time with a comment claiming it survives an id rename for persistence, but neither editor's actual Save path (`SyncFactionBuildings`/`SyncFactionUnits`) ever reads it — both match on-disk objects by the definition's CURRENT (post-edit) id, so renaming an id and saving via the simple form treats the on-disk record as orphaned and recreates a fresh JSON object at the new id, silently dropping any truly-unmodeled/custom JSON key that lived on the old-id object outside the `UnitDefinition`/`BuildingDefinition` schema.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/Core/Definitions/FactionWriter.cs (SyncFactionUnits, SyncFactionBuildings), godot/src/CreationSuite/BuildingCardPanel.cs, godot/src/CreationSuite/UnitCardPanel.cs
reason: summary: an id rename followed by a simple-form Save can silently drop truly-unmodeled custom JSON keys because `_originalId` is captured but never used for on-disk lookup. evidence: Confirmed identical, unused pattern already present in `UnitCardPanel.cs` since Story 3.4 (`_originalId`'s doc comment there makes the same "survives an id rename" claim the Sync path doesn't honor) — not a new defect. All schema-modeled fields (including `combat_feedback`) still round-trip correctly via `ApplyFields`/`ApplyBuildingFields` on the fresh object; only a genuinely out-of-schema hand-added key would be lost, and no shipped content has one. Closure: either make `SyncFactionUnits`/`SyncFactionBuildings` match by `_originalId` first (falling back to current id for new entries), or remove the misleading doc-comment claim if the current-id-match behavior is intentionally accepted. Flagged by the Blind Hunter and Edge Case Hunter review layers.
status: done 2026-07-16
resolution: closed by human decision: Delete the misleading doc comment and accept current-id match; no shipped content carries out-of-schema keys.
decision: 2026-07-16 Remove the claim — Delete the misleading doc comment and accept current-id match; no shipped content carries out-of-schema keys.

### DW-67: `FactionWriter.BuildingEdit`/`BuildingEditKind`/`PatchFactionBuildingJson` (this story's buildings-array counterpart to `UnitEdit`/`UnitEditKind`/`PatchFactionJson`) are exercised only by `FactionWriteRoundTripTests` — `BuildingCardPanel`'s actual Save path calls only `SyncFactionBuildings`, never `PatchFactionBuildingJson`, leaving ~150 lines of the single-edit API surface with no production caller.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/Core/Definitions/FactionWriter.cs
reason: summary: the single-edit `PatchFactionBuildingJson`/`BuildingEdit` writer surface has no current production caller, only test coverage. evidence: Deliberate, spec-directed parity with the identical pre-existing pattern — `UnitEdit`/`UnitEditKind`/`PatchFactionJson` have had the exact same "test-only, no panel caller" characteristic since Story 3.4, and this story's spec explicitly directed mirroring that surface for architectural consistency (a future single-edit consumer — e.g. an external tool, or a lighter-weight targeted-patch API — was the presumed rationale for the unit-side original, never realized). Not a functional defect; a maintainability/dead-code question. Closure: either find/document an intended future caller, or fold `PatchFactionJson`/`PatchFactionBuildingJson` down to test-only internal helpers if no production consumer ever materializes. Flagged by the Blind Hunter review layer.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Human decided 2026-07-19 'Keep the public single-edit API for architectural parity'; FactionWriter.PatchFactionBuildingJson/BuildingEdit remain test-only with panels calling SyncFactionBuildings — exactly the accepted parity state. Settled.
decision: 2026-07-19 Keep the public single-edit API for architectural parity

### DW-68: A creator-authored brand-new building (a novel id with no `BuildingType` enum member) still cannot be selected or placed in a live match through any existing runtime path — `BuildingSystem.PlaceBuildingDirect`/`QueueWorkerBuild` resolve a definition via `TechTreeChecker.BuildingTypeId(type)` (no case for `Custom`, returns `""`), and `ScenarioApplier.ParseBuildingType` independently falls back an unrecognized type string to `BuildingType.CommandCenter` — both entry points are still closed-enum-keyed, so the definition this story lets a creator author and save is inert for actual gameplay until a future story threads an authored id through either path.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/Economy/BuildingSystem.cs, godot/src/Core/Sim/ScenarioApplier.cs, godot/src/Core/BuildingStore.cs, godot/src/UI/EntityPlacer.cs
reason: summary: a brand-new (non-enum-backed) building authored via the Building Card Editor round-trips correctly through the loader/writer but cannot yet be placed in a live match by any existing code path. evidence: Cross-references the already-open DW-57 (migrated from Story 4.2's review, which named `BuildingSystem`/`EntityPlacer`'s `Custom`-enum gap and explicitly deferred its closure to "Stories 4.5/4.6"). This story (4.5) independently confirms DW-57 is still unresolved and adds new evidence DW-57 didn't cover: `ScenarioApplier.ParseBuildingType` (the scenario-authored initial-building placement path, distinct from `EntityPlacer`'s live in-match placement) has the SAME closed-enum limitation, silently mis-mapping any unrecognized type string to `CommandCenter` rather than resolving a real definition. This story's own AC3 ("places with exactly the stats authored... round-trips through 4.1's loader") is satisfied for the narrower, textually-anchored reading — an EXISTING enum-backed building's edited stats correctly round-trip to live placement (verified), and a NEW building's DATA correctly round-trips through the loader/writer (verified) — but the epic's broader "no longer locked to four hardcoded building types" promise for actually PLACING a wholly new building type is not complete until a future story (4.6, or an Epic-5 Faction-Definer story) retires the `BuildingType` enum's placement-selection gate, a determinism-sensitive change (`BuildingType` is byte-serialized into replays/scenarios — append-only, never renumbered) well beyond this presentation-layer editor story's "never touch sim arrays" scope. Closure: same as DW-57 — resolved once a later story threads an authored building id through `BuildingSystem`'s and `ScenarioApplier`'s placement-resolution paths instead of the enum. Flagged independently by the Intent-Alignment Auditor review layer (via an independent sub-exploration) and corroborated by this entry's author.
status: done 2026-07-15
resolution: closed by story 6-8 (commit 3703b2d, epic-6 retro audit 2026-07-15) — authored building ids now thread through ScenarioValidator/ScenarioApplier/BuildingSystem placement as `BuildingType.Custom` + `DefinitionId` with def-resolved stats; `ParseBuildingType` no longer collapses unknown ids to CommandCenter; the enum placement gate is retired and all three `(int)BuildingType` array touch-sites are fixed. Remainders split: in-match train-card operability = DW-168; prereq-SOURCE lookup = DW-57 (still open).

### DW-69: Ctrl+Z/Ctrl+Y in `BuildingCardPanel.Edit.cs`/`UnitCardPanel.Edit.cs` fires `_history.Undo()`/`Redo()` without first releasing focus from whatever control currently has it — if the focused control is a required-field `SpinBox` mid-edit (no prior blur), the undo/redo call can interleave with that control's own `FocusExited` commit handler in an unverified order.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/CreationSuite/BuildingCardPanel.Edit.cs, godot/src/CreationSuite/UnitCardPanel.Edit.cs
reason: summary: Ctrl+Z/Y while a field control still has focus is a theoretical reentrancy/ordering edge case with no confirmed repro. evidence: The undo/redo input wiring is mirrored verbatim from the proven `UnitCardPanel.Edit.cs` pattern (same `_panel.Visible` gate, same `SetInputAsHandled` call) — not a new risk introduced by this story, and not demonstrated as an actual bug (speculative, flagged by static reading of the input-handling order rather than a live repro). Closure: if ever confirmed reproducible, add an explicit `ReleaseFocus()` before `_history.Undo()/Redo()` in both editors. Flagged by the Edge Case Hunter review layer.
status: open

### DW-70: `BuildingCardPanel.Edit.cs`'s `ShowBadge` has no field-control "home" for any inherited unit-only validation key (`is_hero`, `hero.*`, `sells_items`, `shop_stock`, `shop_radius`, `revives_heroes`) that the merged `UnitDefinitionValidator` gate can still emit against a `BuildingDefinition` edited via the raw-JSON hatch — such an error leaves Save permanently disabled with the status line's error count correct but no visible badge pinpointing which field to fix.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/CreationSuite/BuildingCardPanel.Edit.cs
reason: summary: a raw-JSON-authored building with an invalid inherited unit-only field (e.g. `is_hero:true` with no `hero` block) has no badge home, leaving Save blocked with an unlocatable error. evidence: Mirrors the identical, already-accepted `ShowBadge` fallback comment in `UnitCardPanel.Edit.cs` ("no field control home for this key (should not happen)") — an inherited, low-priority gap class, not novel to this story. Very low real-world reachability for buildings specifically: no shipped building authors any hero/shop field, and the raw-JSON hatch is the only path that could introduce one. Closure: either give the Advanced tab a generic "other validation errors" list surfacing any unbadged (FieldPath, Message) pair by name, or accept the gap as-is (matches existing unit-editor precedent). Flagged by the Edge Case Hunter review layer.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Human decided 2026-07-19 'Accept the gap (matches existing unit-editor precedent)'; the unbadged inherited unit-only validation-key class is a low-reachability gap reachable only via the raw-JSON hatch, and the decision was to accept it. Settled.
decision: 2026-07-19 Accept the gap (matches existing unit-editor precedent)

### DW-71: `BuildingCardPanel.Edit.cs`'s `AddRequiredNumFloat`/`AddRequiredNumInt` composites (new for this story — buildings are the first `UnitDefinition`-family content type with nullable-until-authored required fields) silently commit the field at its currently-displayed value (0) the moment focus leaves the control, even if the creator only clicked in and back out without an intentional edit.
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/CreationSuite/BuildingCardPanel.Edit.cs
reason: summary: merely tabbing into then out of an unauthored required numeric field silently authors it at 0. evidence: Genuinely novel to this story (no prior nullable-required-field UI pattern existed to mirror) — not destructive (0 is a legitimate authored value for both `construction_time` and `supply_bonus`, per `BuildingDefinitionValidator`'s own "author 0 for a building that grants no supply" doc comment), but surprising: a creator can't "peek" at a required field without permanently committing it. Closure: only commit on an actual value CHANGE (track the value at focus-enter and compare at focus-exit), not on blur alone. Flagged by the Blind Hunter review layer.
status: open
decision: 2026-07-19 Commit only on real change — Drop the null->displayed-value confirm on blur so tabbing in/out of an unauthored required field leaves it null; only a ValueChanged authors it.

### DW-72: `UnitCardPanel.Edit.cs`'s `CloneUnit` still does not copy `SellsItems`/`ShopStock`/`ShopRadius` (Story 3.16 shop fields) onto a duplicated unit — discovered incidentally while fixing this story's `CloneUnit`-missing-`Cost` finding (a related but distinct field-completeness gap in the same method).
source_spec: `_bmad-output/implementation-artifacts/spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md`
location: godot/src/CreationSuite/UnitCardPanel.Edit.cs
reason: summary: duplicating a unit authored as a shop (Story 3.16) silently strips its sells_items/shop_stock/shop_radius fields on the clone. evidence: Pre-existing since Story 3.16 added these fields to `UnitDefinition` — `CloneUnit` (a Story 3.4 method, `UnitCardPanel.Edit.cs`, read-only reference for this story per its own scope) was never updated for them, unlike this story's `CloneBuilding`, which was written comprehensively against the current field set and does include them. Unlike the `Cost` field (fixed in this same review pass, since this story's `PutCostMap` change newly ACTIVATES that dormant bug into an observable one), these shop fields' persistence was already fully working before and after this story — the gap is real but was not newly surfaced/caused by this diff, so it is deferred rather than patched here. Closure: add `SellsItems = s.SellsItems, ShopStock = s.ShopStock?.Clone() as string[], ShopRadius = s.ShopRadius` to `CloneUnit`, mirroring `CloneBuilding`'s equivalent lines. Flagged by the Verification-Gap review layer (incidental discovery during this pass's patch work).
status: open

## Deferred from: code review of story-4.6 (2026-07-09)

### DW-73: `TechTreePanel`'s interactive behavior — connection/disconnection handlers, node-selection-to-inspector wiring, `Persist()`'s atomic write path, and the reload round-trip — has zero automated test coverage; only the two pure Godot-free helpers underneath it (`TechTreeLayout.ComputeTiers`, `TechTreeValidator.ValidateProposedEdge`) are unit-tested.
source_spec: `_bmad-output/implementation-artifacts/spec-4-6-visual-tech-tree-editor-tier-laned-graph-drag-out-port-to-wire-prerequisites.md`
location: godot/src/CreationSuite/TechTreePanel.cs
reason: summary: the AC's interactive/persistence/reload-round-trip behaviors are only verified manually, never by an automated regression test. evidence: consistent with the pre-existing lack of automated interaction tests across every CreationSuite editor (`BuildingCardPanel`, `UnitCardPanel`, `TriggerEditorPanel`, etc. — none have automated GraphEdit/Control-drag tests; the project's two-tier testing model only requires sim-layer logic to be Godot-free-testable). This story's own manual `/godot-verify` pass exercised all 8 I/O-matrix scenarios end-to-end (including the persistence round-trip and reload), so functional correctness at ship time is verified, but nothing guards against a future regression in the GraphEdit wiring itself. Closure: if/when this codebase adopts an automated harness for driving Godot Control/GraphEdit interaction (none exists today for any editor), extend it to `TechTreePanel`'s connect/disconnect/select/persist/reload paths. Flagged by the Blind Hunter and Intent Alignment Auditor review layers.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — A Godot Control/GraphEdit interaction test the project's two-tier testing model deliberately excludes (only sim-layer logic must be Godot-free-testable); no automated harness for driving Godot Control interaction exists for any CreationSuite editor, so it is not actionable without first building one (no named story does).

### DW-74: `TechTreePanel` subscribes to `GameState.ModeChanged` in `Initialize` with no corresponding unsubscribe (no `_ExitTree` override), risking a dangling event handler if the panel is ever freed independently of `GameState`.
source_spec: `_bmad-output/implementation-artifacts/spec-4-6-visual-tech-tree-editor-tier-laned-graph-drag-out-port-to-wire-prerequisites.md`
location: godot/src/CreationSuite/TechTreePanel.cs
reason: summary: no unsubscribe path exists for the ModeChanged subscription. evidence: identical, pre-existing pattern already used unchanged by `BuildingCardPanel`, `UnitCardPanel`, and `TriggerEditorPanel` (none of the three unsubscribe either) — not introduced or worsened by this story, and not a live incident in the current bootstrap lifecycle (all panels share `GameState`'s lifetime, added once per scene, never freed independently). Closure: if ever adopted, add a shared unsubscribe convention (e.g. an `_ExitTree` override) across all CreationSuite panels in one pass, not just this one. Flagged by the Edge Case Hunter and Blind Hunter review layers.
status: open

### DW-75: `TechTreePanel` wires no Ctrl+Z/Y undo/redo for edge add/remove, unlike `BuildingCardPanel`/`UnitCardPanel` which both support undo for their field edits — an accidental drag-to-disconnect has no undo path beyond manually re-dragging the same edge back.
source_spec: `_bmad-output/implementation-artifacts/spec-4-6-visual-tech-tree-editor-tier-laned-graph-drag-out-port-to-wire-prerequisites.md`
location: godot/src/CreationSuite/TechTreePanel.cs
reason: summary: no undo/redo exists for tech-tree edge mutations, unlike sibling editors. evidence: epics.md's Story 4.6 acceptance criteria never mention undo/redo for the tech-tree editor, so this isn't a spec gap this story's own text should have caught; it's a UX-consistency gap relative to sibling editors' precedent. Recoverable (re-dragging the same edge restores it) rather than destructive, so low severity. Closure: if creator feedback confirms this is a real pain point, extend the existing per-editor undo-history pattern (already used by `BuildingCardPanel`/`UnitCardPanel`) to `TechTreePanel`'s edge mutations. Flagged by the Blind Hunter review layer.
status: open
decision: 2026-07-17 Add edge undo — Extend the per-editor EditorHistory undo pattern to TechTreePanel edge add/remove.
decision: 2026-07-16 Add edge undo — Extend the per-editor EditorHistory undo pattern to TechTreePanel edge add/remove.

### DW-76: `TechTreePanel` hardcodes `CanvasLayer { Layer = 9 }` justified only by an inline comment ("below BuildingCardPanel's 13") rather than a shared constant/registry — the same ad-hoc pattern every existing CreationSuite panel already uses, so layer collisions among panels are already possible today and not introduced by this story.
source_spec: `_bmad-output/implementation-artifacts/spec-4-6-visual-tech-tree-editor-tier-laned-graph-drag-out-port-to-wire-prerequisites.md`
location: godot/src/CreationSuite/TechTreePanel.cs
reason: summary: no shared registry backs any CreationSuite panel's CanvasLayer number. evidence: verified `MapGeneratorPanel` and `BuildingCardPanel` both already use `Layer = 13`, and `UnitCardPanel`/`ItemCardPanel`/`PersistenceManifestPanel` all already use `Layer = 11` — pre-existing duplication with no reported incident. Closure: if panel z-order bugs are ever observed, centralize all CreationSuite panel layer numbers into one shared constants class in a dedicated pass covering every panel, not just new ones. Flagged by the Blind Hunter review layer.
status: open

## Deferred from: code review of story-4.7 (2026-07-09)

### DW-77: `EntityPlacer.PlaceResourceNode` still calls the legacy 4-arg `ResourceNodeStore.Create` overload — a creator placing a resource node through the in-app tool has no way to author `collection_model`/`resource_type`/`requires_structure`/`owner_slot`/`income_period_ticks`; every node placed through the actual creator-facing tool is hard-defaulted to GATHER/Ore/no-gate/Neutral, so today only hand-edited scenario JSON can reach this story's new capability.
source_spec: `_bmad-output/implementation-artifacts/spec-4-7-per-resource-collection-models-income-streaming-requires-structure-crystal-production.md`
location: godot/src/UI/EntityPlacer.cs
reason: summary: the in-app resource-node placement tool cannot author any of the 6 new Story 4.7 fields. evidence: `EntityPlacer.cs` is untouched by this story's diff (pre-existing tool, not regressed) — Story 4.7's own acceptance criteria (epics.md) are phrased entirely at the `ScenarioResourceNode`/sim-engine data surface ("Given a ScenarioResourceNode declaring collection_model=..."), matching FR-15's "as data" framing and the epic's established pattern of separating schema/sim stories (4.1/4.2/4.3/4.4) from later in-app-editor stories (4.5/4.6) — Epic 6's Story 6.4 ("verify entity, start-position, resource-node, and win-condition placement to ship bar") is the documented likely closure point for wiring the placement tool to the new fields. Flagged by the Intent Alignment Auditor.
status: open

### DW-78: `EntityWorld.GatherState`/`CarryAmount`/`GatherTarget` (a worker's in-flight gather state) remain entirely unfolded from `SimChecksum`, even after this story's extensive checksum-coverage work on `ResourceNodeStore` — a worker's carried-resource load or gather-cycle state can diverge between clients undetected.
source_spec: `_bmad-output/implementation-artifacts/spec-4-7-per-resource-collection-models-income-streaming-requires-structure-crystal-production.md`
location: godot/src/Core/SimChecksum.cs, godot/src/Core/EntityWorld.cs
reason: summary: per-worker gather state (GatherState/CarryAmount/GatherTarget) is not folded into SimChecksum. evidence: pre-existing since `GatheringSystem`'s worker state machine was first built (well before Story 4.7) — this story's fold work was scoped to `ResourceNodeStore` (the net-new mutable node-side state), not the pre-existing worker-side fields, which were never in this story's Code Map. Surfaced incidentally while reviewing this story's checksum-coverage additions. Closure: fold `GatherState`/`CarryAmount`/`GatherTarget` into `SimChecksum`'s per-entity loop in a future desync-hardening pass. Flagged by the Blind Hunter review layer.
status: open
decision: 2026-07-27 Fold gather state into SimChecksum — Add GatherState/CarryAmount/GatherTarget/CarryResourceType to SimChecksum's per-entity loop and re-record the affected goldens on Windows, tightening the desync tripwire to catch a gather-only divergence the tick it happens.
decision: 2026-07-28 correct-course — bundle gather-state-checksum-fold (Epic 15, Story 15.4); golden re-record on Windows

### DW-79: `GatheringSystem.FindBestNode`'s `requires_structure` gate check (`StructureGateOpen` → `FactionHasStructureNear`) performs a full `BuildingStore` scan per candidate node per Idle worker per tick — an O(nodes × buildings) cost whenever any node is gated, versus the pre-4.7 O(nodes) scan.
source_spec: `_bmad-output/implementation-artifacts/spec-4-7-per-resource-collection-models-income-streaming-requires-structure-crystal-production.md`
location: godot/src/Economy/GatheringSystem.cs
reason: summary: the new requires_structure gate check adds an O(buildings) inner scan to FindBestNode's per-node loop. evidence: bounded by `ResourceNodeStore.MAX_NODES` (64) and `BuildingStore.MAX_BUILDINGS` (64) today — worst case ~4096 ops per Idle worker per tick, negligible at this project's target scale (500-2000 entities @ 30 ticks/sec per root CLAUDE.md), and only Idle workers (not every entity) trigger it. Same "theoretical at current scale" class as the already-accepted DW-59 (TechTreeValidator's recursive DFS). Closure: revisit with a spatial index (mirrors any future SpatialHash-based query) if content scale or node/building counts ever grow enough for this to matter. Flagged by the Blind Hunter review layer.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Perf observation the entry itself classes as an accepted 'theoretical at current scale' deferral (bounded MAX_NODES×MAX_BUILDINGS, only Idle workers trigger it). No incident; revisit only if a spatial index is introduced or content scale grows.

### DW-80: A Streaming worker whose `requires_structure` gate closes permanently (the gating structure destroyed and never rebuilt) stays parked in `GatherState.Gathering` indefinitely, producing zero credit, rather than ever being freed to seek a different eligible node.
source_spec: `_bmad-output/implementation-artifacts/spec-4-7-per-resource-collection-models-income-streaming-requires-structure-crystal-production.md`
location: godot/src/Economy/GatheringSystem.cs
reason: summary: a Streaming worker at a permanently gate-closed node never re-idles to seek another node. evidence: a defensible reading of AC4's "credit is withheld... becomes eligible and begins producing" (same worker, same node resumes — not "worker reassigns elsewhere"), now proven-as-implemented by `RequiresStructure_StreamingGate_ClosesThenReopensMidGather_WithholdsThenResumesCredit` (this story's own review-patch test). Not exercised by any shipped scenario (none author `requires_structure` yet — see DW-77). Closure: a future design ruling on whether a permanently-gated Streaming worker should eventually re-idle and seek a different node (matching GATHER's node-vanishes-mid-cycle re-seek behavior) versus staying parked awaiting the same structure's return. Flagged by the Verification Gap Reviewer and Edge Case Hunter.
status: open
decision: 2026-07-20 Re-idle and re-seek — After N ticks of a closed gate, free the worker to Idle and seek a different eligible node (matching GATHER's node-vanishes re-seek).
decision: 2026-07-16 Re-idle and re-seek — After N ticks of a closed gate, free the worker to Idle and seek a different eligible node (matching GATHER's node-vanishes re-seek).

### DW-81: Follow-up review still recommended for 4-7-per-resource-collection-models-income-streaming-requires-structure-crystal-production after the review budget was exhausted
origin: review-budget-followup
source_spec: `spec-4-7-per-resource-collection-models-income-streaming-requires-structure-crystal-production.md`
severity: low
reason: Review budget (2 cycles) was exhausted with the story finalized (status: done, verify green) while the review pass kept recommending an independent follow-up. The work was committed by bmad-loop run 20260709-202815-6ca2; this entry preserves the lingering follow-up recommendation for a deliberate later review.
status: done 2026-07-16
resolution: closed by human decision: Story shipped and verified green; accept the residual review recommendation as satisfied.
decision: 2026-07-16 Close as reviewed — Story shipped and verified green; accept the residual review recommendation as satisfied.

### DW-82: No round-trip test exists anywhere for `BuildingDefinition.Prerequisites` itself, so Story 4.8's "round-trips exactly like `Prerequisites`" claim (AC2) can't be checked against a shared, comparable assertion.
source_spec: `_bmad-output/implementation-artifacts/spec-4-8-researchdefinition-content-model-validation.md`
location: godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs
reason: summary: `Prerequisites`'s own round-trip behavior is unproven by any test, so the parity claim for the new `AvailableResearch` field rests only on "reads the same `PutStringArray` call," not a shared assertion that would catch the two diverging later. evidence: `grep -rl Prerequisites` across `ProjectChimera.Sim.Tests/Definitions` finds only tech-tree/layout/this-story's-new-file tests, no `Prerequisites`-specific round-trip test. Pre-existing gap (predates this story) surfaced incidentally by review. Closure: add a `Prerequisites` round-trip test alongside the existing `AvailableResearch` ones so both fields share one proven contract. Flagged by the Blind Hunter review layer.
status: open
decision: 2026-07-28 correct-course — bundle content-package-import-roundtrip merged into map-package-import-one-path (DW-235; Epic 15, Story 15.6)

### DW-83: `ModifierStore.Apply` silently drops a new modifier install when an entity's fixed 8-slot ring (`EffectCaps.MaxModifiersPerEntity`) is already full — no event, no log — and Story 4.9's permanent research modifiers are now one more producer that can hit this ceiling alongside item stat modifiers, hero growth stacks, and ability self-passives.
source_spec: `_bmad-output/implementation-artifacts/spec-4-9-researchsystem-order-path-start-complete-cancel-modifier-application.md`
location: godot/src/Effects/ModifierStore.cs (`Apply`'s slot-full branch), godot/src/Economy/ResearchSystem.cs (`ApplyCumulativeModifier`)
reason: summary: a unit simultaneously holding several item modifiers, a self-passive, AND multiple distinct completed researches (each research = its own modifier id = its own slot) can silently lose an earned permanent research buff with no player-visible signal once the ring is full. evidence: `ModifierStore.Apply`'s own doc comment states the slot-full behavior is deterministic-refuse-not-overflow "(drops it; never overflows the per-entity ring)" — an existing, accepted architectural limit shared by every modifier producer (e.g. `HeroXpSystem`'s growth stacking hits the same ceiling), not something Story 4.9 introduces uniquely; research just becomes one more path that can trigger it. Pre-existing `ModifierStore` design, surfaced incidentally by this story's review (Blind Hunter + Edge Case Hunter, independently). Closure: either raise `EffectCaps.MaxModifiersPerEntity`, add a starvation/eviction policy, or at minimum surface a diagnostic event on a refused install so a full ring is debuggable instead of silent.
status: open
decision: 2026-07-20 Diagnostic on refusal — Emit a diagnostic event/log on a refused install so a full ring is debuggable instead of silent.
decision: 2026-07-16 Diagnostic on refusal — Emit a diagnostic event/log on a refused install so a full ring is debuggable instead of silent.

### DW-84: `MatchLifecycleController`'s per-match wiring of shared systems into `LockstepManager`/`ReplayPlayer` (`Buildings`, `Items`, and now `Research`) has zero test coverage for any of these assignments — a dropped or mis-copied line would silently break online/replay parity for that command family with no test catching it.
source_spec: `_bmad-output/implementation-artifacts/spec-4-9-researchsystem-order-path-start-complete-cancel-modifier-application.md`
location: godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs, godot/src/Multiplayer/LockstepManager.cs, godot/src/Multiplayer/ReplayPlayer.cs
reason: summary: `grep -rl "MatchLifecycleController" | grep -i test` finds no test file at all — the `_ctx.Lockstep.Buildings = _ctx.BuildSys` / `_ctx.Lockstep.Items = _ctx.Host.ItemSys` lines (pre-existing, Stories 2.8/3.15) and the new `_ctx.Lockstep.Research = _ctx.Host.ResearchSys` / `_ctx.ReplayPlayer.Research = _ctx.Host.ResearchSys` lines this story added are all equally unverified. evidence: confirmed by both the Blind Hunter and Verification Gap review layers independently; the gap predates Story 4.9 (Buildings/Items were never covered either) and this story's addition merely extends an existing, unaddressed testing debt rather than introducing a new one. Closure: a test that constructs the real bootstrap chain (or a narrow seam around `MatchLifecycleController.OnMatchStart`/`TryLoadReplay`) and asserts `_ctx.Lockstep.Buildings/Items/Research` and `_ctx.ReplayPlayer.Buildings/Items/Research` all reference the SAME instances as `_ctx.Host`'s, closing this for every wired system at once rather than one at a time.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — MatchLifecycleController is Godot-dependent (using Godot; ISetupPhase), so the Godot-free sim test project cannot reference it — the same two-tier-model exclusion as DW-73. The forwarding half of this risk is covered buildably by DW-86; testing the wiring would require extracting a Godot-free seam first.

### DW-85: Completing a research level with a positive `max_health_delta` burst-heals every currently-alive faction unit by the FULL cumulative max-health bonus on every completion (not the level's increment), because `ResearchSystem.ApplyCumulativeModifier` does `RemoveByModifierId` + `Apply` and `ModifierStore.ApplyStatDeltas` heals current Health on any positive-MaxHealth apply (the documented Decision #3).
source_spec: `_bmad-output/implementation-artifacts/spec-4-9-researchsystem-order-path-start-complete-cancel-modifier-application.md`
location: godot/src/Economy/ResearchSystem.cs (`ApplyCumulativeModifier` — remove-then-reapply), godot/src/Effects/ModifierStore.cs:456-466 (`ApplyStatDeltas` heal-on-apply)
reason: summary: a damaged unit below its base+prior-bonus max is healed by the entire cumulative max-health delta each time a +MaxHealth research level completes, turning a repeatable +HP research into a repeatable army-heal — whereas the cited cumulative precedent (`HeroXpSystem.ReconcileGrowth`) heals only the per-level increment because it applies growth INCREMENTALLY via `StackRule.Stack` (one `Apply` per new stack), never remove-then-reapply-the-full-cumulative. evidence: verified by reading both systems: research's `ApplyCumulativeModifier` calls `RemoveByModifierId(id)` (clamps Health DOWN only, `isApply:false`) then `Apply(fullCumulative)` (heals Health UP by the full new delta, `isApply:true`); for a unit already below the old ceiling the remove is a no-op and the apply heals by the full cumulative. The heal-on-apply itself is a pre-existing, deliberately-documented engine behavior (Decision #3) shared by every +MaxHealth modifier producer (items, hero growth) — research is one more producer, but its spec-MANDATED `StackRule.Refresh`/single-cumulative-slot design (which explicitly forbids `StackRule.Stack`) is what forces the remove-then-reapply and thus the full-cumulative heal. Deterministic (no desync), no crash; content-dependent — no shipped research authors `max_health_delta` today (tests exercise Armor/AttackDamage only). Flagged by the Blind Hunter review layer. Closure: a design ruling is required — accept it, heal-by-increment (snapshot Health across the remove/reapply and re-add only the level's delta), or suppress the heal for research re-applies; the intent contract is silent on current-Health-on-completion and there is no single defensible reading to auto-patch.
status: done 2026-07-16
resolution: already resolved: ResearchSystem.cs:314-317 living-army completion loop calls ApplyCumulativeModifier(..., preserveCurrentHealth: true); :350 snapshots healthBefore and :356-357 restores Fixed.Clamp(healthBefore,0,EffectiveMaxHealth) after remove+reapply — the burst-heal is suppressed for the completion path (future-spawn catch-up keeps the heal via false at :404)

### DW-86: The research command family (`StartResearch`/`CancelResearch`) has no replay-vs-live round-trip parity test, unlike every other command family (`BuyItem`/`UseItem`/`DropItem`/`SetRally` in `CommandApplyParityTests`) — a dropped `Research` forwarding arg in `ReplayPlayer.ApplyOrders` (or `LockstepManager.ApplyOrders`) would leave the whole suite green while silently making recorded replays and online matches stop applying research (offline still would), a desync.
source_spec: `_bmad-output/implementation-artifacts/spec-4-9-researchsystem-order-path-start-complete-cancel-modifier-application.md`
location: godot/src/Multiplayer/ReplayPlayer.cs:189, godot/src/Multiplayer/LockstepManager.cs:680, godot/ProjectChimera.Sim.Tests (no CommandApplyParityTests research case)
reason: summary: this story added `Research` forwarding into both `ReplayPlayer.ApplyOrders` and `LockstepManager.ApplyOrders`, but the only tests that drive `StartResearch`/`CancelResearch` through `OrderApplier.Apply` inject `research:` by hand and never go through `ReplayPlayer`/`LockstepManager`, so the forwarding lines are unverified. evidence: `CommandApplyParityTests` owns the replay-round-trip pattern (`new ReplayPlayer(path, world) { Items = ..., Buildings = ... }; player.Flush(1)`) for BuyItem/UseItem/DropItem/SetRally but has no `Research` reference at all; deleting the `, Research` arg at `ReplayPlayer.cs:189` makes replayed research commands deterministically no-op (research defaults null) while offline still applies them — the exact AR-17 replay-vs-live divergence class that suite exists to prevent. Distinct from DW-84 (which covers the bootstrap ASSIGNMENT lines, not the `ApplyOrders` forwarding). Flagged by the Verification Gap review layer. Closure: add a `ReplayVsLive_StartResearch_ApplyIdentically` (and Cancel) round-trip to `CommandApplyParityTests`, mirroring the BuyItem case, wiring `{ Research = ... }` and asserting the replayed `ResourceStore`/`ResearchStore` match the live world — deferred rather than auto-patched because authoring a correct replay-recording round-trip unattended risks a vacuous/flaky test.
status: open

### DW-87: `ResearchStore`'s per-faction arrays (and the new v14 `SimChecksum` fold that reads them) are hard-capped at `FACTION_COUNT = 5` (indices 0-4), while `FactionRegistry`/`PLAYER_COUNT` allow up to 8 active players — a >4-active-faction match would `IndexOutOfRangeException` in the new fold loop's `research.InProgressIndex[(int)f]` (and the four sibling arrays) with no bounds guard.
source_spec: `_bmad-output/implementation-artifacts/spec-4-10-researchstore-simchecksum-fold-golden-rebaseline.md`
location: godot/src/Core/SimChecksum.cs (the v14 ResearchStore fold's `foreach (Faction f in factions.ActiveFactions)` loop), godot/src/Core/ResearchStore.cs (`FACTION_COUNT = 5`)
reason: summary: the crash is reachable only once a match actually activates a 5th+ faction, which no shipped scenario does today, and the exact same unguarded-index pattern already exists for `ResourceStore`'s per-faction fold (`SimChecksum.cs`'s Ore/Crystal/SupplyUsed/etc. loop, since Story 1.3b) and `ModifierStore`/other per-faction stores — this story's fold is one more consumer of a pre-existing, already-accepted architectural ceiling, not a new defect it introduces. evidence: `ResearchStore`'s ctor hardcodes `FACTION_COUNT = 5`; `EnsureCapacity` silent-no-ops out-of-range factions defensively, but the fold's direct array indexing has no equivalent guard. Flagged independently by the Blind Hunter and Edge Case Hunter review layers. Closure: when 5+ active factions become real (tracked wherever `FactionRegistry.PLAYER_COUNT`'s 8-player ceiling is actually wired up), widen `ResearchStore`/`ResourceStore`/every other per-faction store's fixed arrays together in one pass, or add a shared bounds-checked accessor.
status: done 2026-07-24
resolution: already resolved: ResearchStore.cs:24 FACTION_COUNT = FactionRegistry.FACTION_ARRAY_SIZE (9); arrays sized 57-64; SimChecksum.cs:535 fold indexes max Player8=8<9. Story 9.2 widening.

### DW-88: `ResearchSystem.CompleteResearch` (Story 4.9) scans every entity up to `world.HighWaterMark` (up to 4096) on every single research completion to apply the cumulative modifier faction-wide, an O(n) full-world scan on a comparatively rare, event-driven action rather than a per-tick one.
source_spec: `_bmad-output/implementation-artifacts/spec-4-10-researchstore-simchecksum-fold-golden-rebaseline.md`
location: godot/src/Economy/ResearchSystem.cs (`CompleteResearch`'s entity loop)
reason: summary: the code comment justifies the ascending-id full-world scan by citing `SupplySystem.Tick`'s identical loop shape as precedent, but `SupplySystem` runs that scan every tick by design, while research completion is comparatively rare — the precedent undersells the cost difference and the loop is pre-existing (Story 4.9), unchanged by this story's checksum fold. evidence: read directly by the Blind Hunter review layer during this story's review; not a correctness defect (deterministic, ascending-id, no desync risk) — a performance characterization/comment-accuracy observation only, and out of this story's scope (which touches only the checksum fold, not `ResearchSystem`'s completion logic). Closure: if profiling ever shows this loop as hot (unlikely at today's `MAX_BUILDINGS`/unit-count scale), consider maintaining a per-faction alive-unit index instead of a full-world scan; at minimum, correct the comment's precedent framing.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — A performance-characterization / comment-accuracy observation, not a correctness defect (deterministic, ascending-id, no desync); negligible at current MAX scale and an accepted 'revisit only if profiling shows it hot' deferral like DW-79. Only optional residue is a one-line comment reframing.

### DW-89: No uniqueness check prevents a research id from colliding with an existing building id (or vice versa) before both are added as same-`GraphEdit` `GraphNode`s — Godot's `AddChild` auto-renames the second node on a `Name` collision, silently breaking `TechTreePanel`'s by-name edge/selection resolution (`ConnectNode`, `OnConnectionRequest`, `OnNodeSelected`) for whichever entry lost the name.
source_spec: `_bmad-output/implementation-artifacts/spec-4-11-research-authoring-command-card-buttons-upgrade-display.md`
location: godot/src/CreationSuite/TechTreePanel.cs (RebuildGraph's building-node loop :229-238 and research-node loop :263-275 both set `Name = <id>` on the SAME GraphEdit with no cross-check), godot/src/CreationSuite/TechTreePanel.cs:427 (`OnNodeSelected`'s `FirstOrDefault` resolution is first-wins, inconsistent with RebuildGraph's `researchById[r.Id] = r` last-wins dict at :258 — the same underlying duplicate-id class manifests two different ways)
reason: summary: before this story, only building nodes existed in this GraphEdit, so a name collision was impossible by construction (BuildingDefinitionValidator already rejects duplicate building ids); this story introduces a SECOND id-namespace (research) sharing the same node-name space, and no validator checks a research id against the building id set or vice versa. evidence: confirmed by reading TechTreePanel.cs directly — `researchById` is built with last-wins semantics (`researchById[r.Id] = r`) while `OnNodeSelected` re-resolves via `_faction.Research?.FirstOrDefault(...)` (first-wins), so a duplicate id even resolves inconsistently WITHIN this story's own code, independent of the Godot auto-rename question. Flagged independently by the Blind Hunter and Edge Case Hunter review layers. Likely low-frequency (requires a creator to reuse an id across the two separately-edited BuildingCardPanel/ResearchCardPanel forms) and gracefully degrading (Godot auto-renames rather than crashing), so not patched inline pending a design call. Closure: add a cross-namespace uniqueness check (e.g. in `ResearchValidator.Validate` and/or `BuildingDefinitionValidator.Validate`, checking the OTHER definition list's id set) with a located error, and fix `OnNodeSelected` to resolve via the same last-wins `researchById`-style lookup `RebuildGraph` already uses rather than a fresh `FirstOrDefault`.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening
decision: 2026-07-20 Cross-namespace check + fix resolution — Add a cross-namespace uniqueness check (located error) in the validators AND fix OnNodeSelected to resolve via the same last-wins lookup RebuildGraph uses.
decision: 2026-07-16 Cross-namespace check + fix resolution — Add a cross-namespace uniqueness check (located error) in the validators AND fix OnNodeSelected to resolve via the same last-wins lookup RebuildGraph uses.

### DW-90: No mutual exclusivity between the command card's Research button grid and the existing Shop/Revive/Train button grids for the same building — all are gated only by their own single condition (`!canProduce`-style checks) and render at the same screen coordinates, so a building authored with both `AvailableResearch` entries and e.g. `ShopStock` would draw overlapping button grids with nothing preventing it.
source_spec: `_bmad-output/implementation-artifacts/spec-4-11-research-authoring-command-card-buttons-upgrade-display.md`
location: godot/src/UI/CommandCardSystem.cs (RefreshResearchButtons and its Shop/Revive/Train sibling Refresh* methods, all positioning at the same base offset e.g. `10f + i*102f, 74f`)
reason: summary: this is a pre-existing architectural pattern — Shop and Revive/Train grids already coexist with the same single-condition-gating, same-coordinate-sharing shape with no cross-grid exclusivity check anywhere in this file, predating this story. Story 4.11's Research grid follows the SAME established (already-accepted) convention rather than introducing a new gap. evidence: flagged by the Blind Hunter review layer; verified the Shop/Revive/Train grids share the identical structural shape (independent boolean gate, no shared "which grid is active" state) before this story touched the file. Whether a real building would ever author both `AvailableResearch` and `ShopStock` is a content-authoring/game-design question outside this story's or this review's scope. Closure: if a building ever legitimately needs both, either the categories need a shared mutual-exclusivity gate (e.g. a building declares ONE "produces" category) or the button grids need distinct screen regions — a design decision, not a code defect, and applies equally to the pre-existing Shop/Revive/Train combinations this story didn't create.
status: open
decision: 2026-07-27 One active producer category per building — Add a 'produces' category to BuildingDefinition and gate all producer grids on the single declared category so only one grid renders.
decision: 2026-07-20 One producer-category gate — Have a building declare one active 'produces' category and gate the grids on it.
decision: 2026-07-16 One producer-category gate — Have a building declare one active 'produces' category and gate the grids on it.
decision: 2026-07-28 correct-course — joins bundle command-card-producer-surfaces (Epic 15, Story 15.9)

### DW-91: The command card gates ALL research UI (including the faction-wide in-progress Cancel affordance) on the SELECTED building's own producer/offer status, but research is faction-wide — so (a) a building with `canProduce == true` never shows its authored `AvailableResearch` at all, and (b) once a research is in progress, selecting an owned building that offers no research hides the Cancel button, leaving no way to cancel that order from that card.
source_spec: `_bmad-output/implementation-artifacts/spec-4-11-research-authoring-command-card-buttons-upgrade-display.md`
location: godot/src/UI/CommandCardSystem.cs (`RefreshBuildingCard` research gate at ~:426 `if (!canProduce && _research != null ...) RefreshResearchButtons else HideResearchButtons`; `RefreshResearchButtons` early-return at ~:445 `if (fdef == null || offered.Length == 0) { HideResearchButtons(); return; }` runs BEFORE the in-progress status/Cancel block at ~:456-479)
reason: summary: research state (in-progress/cancel) is a per-faction singleton, but the command card renders and gates it per-building, so a producer building or a non-offering building suppresses UI for state that is logically faction-global. evidence: confirmed by reading `RefreshResearchButtons` — the `offered.Length == 0` early return hides `_researchCancelBtn` even when `anyInProgress`, and the caller's `!canProduce` gate means a barracks-style building that both trains units and lists `AvailableResearch` shows only its train grid, never its research. Both are real but low-consequence: (a) research authored on a producer building is a content-authoring choice that simply won't surface (no crash, no data loss); (b) the player can still cancel by reselecting the building that offers the research, and if that building is destroyed the research completes on its own — being unable to cancel is a lost-refund inconvenience, not a broken state. Distinct from DW-90 (which is about two grids OVERLAPPING on one building; this is about research being HIDDEN/unreachable). Flagged by the Blind Hunter (Cancel reachability) and Edge Case Hunter (producer building) review layers. Closure needs a design call: render the faction-wide in-progress status + Cancel independent of the selected building's offer/producer status (e.g. from `RefreshBuildingCard` whenever any owned building is selected and research is in progress), and decide whether a producing building may co-display a research grid — related to DW-90's "one building, which category is active" question.
status: open
decision: 2026-07-19 Render Cancel/status faction-wide — Show the in-progress status + Cancel whenever any owned building is selected and research is in progress, regardless of the selected building's offer/producer status.
decision: 2026-07-16 Faction-wide research UI — Render the in-progress status + Cancel whenever any owned building is selected and research is in progress, and decide whether a producer building may co-display a research grid (relates to DW-90).

### DW-92: Structural research edits made in `ResearchCardPanel` (Create/Delete/Duplicate) and prerequisite-edge edits made in `TechTreePanel`'s graph both mutate the SAME shared `FactionDefinition` instance, but neither view rebuilds the other — so the on-screen graph and the inspector can drift out of sync until the next Edit↔Play (`R`) toggle rebuilds the graph.
source_spec: `_bmad-output/implementation-artifacts/spec-4-11-research-authoring-command-card-buttons-upgrade-display.md`
location: godot/src/CreationSuite/ResearchCardPanel.cs (`DoCreate`/`DoDelete`/`DoDuplicate` mutate `_faction.Research`), godot/src/CreationSuite/TechTreePanel.cs (`RebuildGraph` only runs on graph (re)build/toggle, not on an inspector-driven research-list change)
reason: summary: two live views (graph + inspector) share one model with no change-notification between them, so a research created/deleted in the inspector leaves a stale node/edges in the visible graph until a manual rebuild. evidence: both `TechTreePanel` and its `_researchInspector` are initialized with the same `_faction` reference; deleting a research entry in the inspector removes it from `_faction.Research` but the already-drawn `GraphNode` and its edges persist until `RebuildGraph` re-runs on the next `R` toggle. Self-healing (a toggle reconciles it), no crash, no persistence corruption — a UX/state-drift polish item, not a defect that reaches disk. Flagged by the Blind Hunter review layer. Closure: have the mutating panel notify/rebuild its sibling (e.g. `ResearchCardPanel` structural edits trigger `TechTreePanel.RebuildGraph`), or route all research-list-structure edits through one owner.
status: open

### DW-93: The command-card research dim predicate (`CommandCardSystem.RefreshResearchButtons`/`FirstUnmetResearchPrereq`) and the unit upgrade-summary math (`SelectionSystem.BuildResearchUpgradeSummary`/`AppendUpgradePart`) are pure, deterministic logic with ZERO automated coverage — the dim predicate is a hand-copied parallel of `ResearchSystem.StartResearchCommand`'s gate chain with nothing pinning the two to agree, and the summary math (skip/order/sign/round-to-zero) is exercised by no test.
source_spec: `_bmad-output/implementation-artifacts/spec-4-11-research-authoring-command-card-buttons-upgrade-display.md`
location: godot/src/UI/CommandCardSystem.cs (`RefreshResearchButtons` gate chain ~:496-506, `FirstUnmetResearchPrereq` ~:552), godot/src/UI/SelectionSystem.cs (`BuildResearchUpgradeSummary` ~:1203, `AppendUpgradePart` ~:1233)
reason: summary: two pieces of this story's new logic are unit-testable independent of Godot but are only covered by code review — a silent inversion (e.g. the completed-level `<= 0` prereq check flipped, a swapped Atk/HP label, or a dropped round-to-zero guard) would ship a wrong readout with green CI. evidence: `grep` finds no test referencing `RefreshResearchButtons`/`FirstUnmetResearchPrereq`/`BuildResearchUpgradeSummary`/`AppendUpgradePart`; `CommandCardSystem`/`SelectionSystem` appear in no file under `ProjectChimera.Sim.Tests`. The intent's Matrix Test Audit waiver covers the LIVE-UI verification gap (click-to-select is blocked by tooling), but that rationale does not address the fact that the underlying pure logic could be extracted to Godot-free helpers and unit-tested — exactly as this same story already did for `ResearchValidator` and `FactionWriter`. This review pass code-reviewed both pieces and found the logic correct (dim chain mirrors `StartResearchCommand`'s order; summary math skips `completed[i] <= 0`, orders Atk/HP/Armor/Spd, omits round-to-zero), so no defect is being carried — this is coverage hardening. Flagged by the Verification Gap and Intent Alignment review layers. Closure: extract the dim-predicate gate evaluation and the upgrade-summary formatting into Godot-free helpers (in Core/Economy), then add unit tests that (a) cross-check the dim predicate's accept/refuse against `ResearchSystem.StartResearchCommand` on shared fixtures and (b) assert the summary string for a known `ResearchStore` state.
status: open

## Deferred from: review of spec-5-1-factionregistry-canonical-faction-slot-constants-ar-3 (2026-07-10)

### DW-94: `FactionRegistry(5..8)` is ctor-valid but `SlotDefinitions`/`GetSlotDefinition` only cover indices `[0,5)`
source_spec: `_bmad-output/implementation-artifacts/spec-5-1-factionregistry-canonical-faction-slot-constants-ar-3.md`
location: godot/src/Core/FactionRegistry.cs (ctor validates `activePlayerCount` against `PLAYER_COUNT=8`; `SlotDefinitions`/`SLOT_DEFINITIONS_SIZE` are hardcoded to 5)
reason: summary: constructing `new FactionRegistry(5..8)` (already ctor-legal today) and then indexing `SlotDefinitions`/`GetSlotDefinition` for `Player5..Player8` silently returns null via the bounds check rather than reflecting real slot data. evidence: same root cause as the existing story-1.3a deferral above (ctor accepts up to `PLAYER_COUNT=8` while the `Faction` enum and every sibling per-faction array top out at 5/Player4) — Story 5.1 adds one more (currently dormant) surface of that pre-existing, already-tracked tension. No live caller constructs `FactionRegistry(5..8)` and then touches `SlotDefinitions` (only `MainScene._Ready`/`BuildHeadlessServerSimHost`, both `FactionRegistry(2)`, populate it). closure: Story 9.2, alongside the enum/array-size widening it already owns — ensure `SLOT_DEFINITIONS_SIZE` grows in lockstep with `PLAYER_COUNT`/`FACTION_ARRAY_SIZE` when the enum widens.
status: done 2026-07-24
resolution: already resolved: FactionRegistry.cs:36 SLOT_DEFINITIONS_SIZE = FACTION_ARRAY_SIZE (9); SlotDefinitions sized to it (:42). Story 9.2 widened enum to Player8.

### DW-95: Bounds-checking for `(Faction)(slot+1)`-derived indices is duplicated three ways, not centralized
source_spec: `_bmad-output/implementation-artifacts/spec-5-1-factionregistry-canonical-faction-slot-constants-ar-3.md`
location: godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs (ResolveSlotFactionDefs, no guard), godot/src/Core/MainScene.cs (BuildHeadlessServerSimHost, has an inline `if ((int)f < 0 || (int)f >= slotDefs.Length) continue;` guard), godot/src/Core/Sim/ScenarioApplier.cs (separate private `InFactionRange` method)
reason: summary: Story 5.1 centralized the `(Faction)(slot+1)` cast itself into `FactionRegistry.ToFaction`, but the range-validation that must accompany every use of that cast's result remains three independently-written checks (one missing entirely, one inline, one a private method on a different class) instead of one canonical bounds-checked path. evidence: pre-existing pattern — none of the three sites' bounds-check shape changed in this diff, only the cast expression did; confirmed by adversarial review (Blind Hunter) and independently by the Edge Case Hunter, who traced `ScenarioLoadPhase.ResolveSlotFactionDefs`'s missing guard as concretely reachable for a scenario slot >= 4. closure: a future story could route all three through `FactionRegistry.GetSlotDefinition`/an equivalent bounds-checked setter, once `ScenarioApplier`'s decoupling from `FactionRegistry` (it takes a raw array, not a registry reference) is revisited — out of Story 5.1's and this deferral's scope to design.
status: done 2026-07-24
resolution: already resolved: MainScene.cs:381 _slotFactionDefs = factions.SlotDefinitions (size 9); ScenarioLoadPhase.cs:376 writes with faction index max Player8=8<9. Story 9.2.
decision: 2026-07-19 Add the missing guard only — Add the inline guard ScenarioLoadPhase lacks (cheap), leaving the three-way duplication in place.
decision: 2026-07-16 Centralize bounds-check — Route all three sites through one FactionRegistry.GetSlotDefinition-style bounded accessor/setter.

### DW-96: `ScenarioLoadPhase.ResolveSlotFactionDefs`'s per-slot write has no bounds guard (pre-existing, same class as the story-1.8c deferral)
source_spec: `_bmad-output/implementation-artifacts/spec-5-1-factionregistry-canonical-faction-slot-constants-ar-3.md`
location: godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:112 (`_ctx.SlotFactionDefs[(int)faction] = def;`)
reason: summary: identical to the already-tracked story-1.8c deferral ("`ResolveSlotFactionDefs` throws `IndexOutOfRangeException` for player slots 4-7 (→ Story 9.2)") — recorded again here because the Edge Case Hunter independently rediscovered it during this story's review and it remains unfixed; Story 5.1's `ToFaction` cast substitution on the line above did not touch this write or add a guard (out of scope — AC3 for this story concerns lookups/reads, not writes). evidence: a scenario player slot >= 4 with a valid on-disk `faction_json` reaches this line before `ScenarioValidator`'s shadow-mode (non-blocking on master) rejection. closure: unchanged from the 1.8c deferral — Story 9.2, or an optional cheap interim guard (`if ((int)faction >= _ctx.SlotFactionDefs.Length) continue;`).
status: done 2026-07-24
resolution: already resolved: ScenarioLoadPhase.cs:376 writes into MainScene.cs:381's size-9 FactionRegistry.SlotDefinitions; valid slots 0-7 -> factions 1-8 all <9. Story 9.2 (named blocker) landed.

## Deferred from: review of spec-5-2-faction-schema-extension-validator-ar-39-ar-12-fr-18-data (2026-07-10)

### DW-97: `FactionValidator.ValidateComplete` (the roster-completeness/mesh_path gate) is not called by any shipped code path — a faction with no Worker unit can launch into a match with zero starting economy, silently
source_spec: `_bmad-output/implementation-artifacts/spec-5-2-faction-schema-extension-validator-ar-39-ar-12-fr-18-data.md`
location: godot/src/Core/Definitions/FactionValidator.cs (`ValidateComplete`, zero production callers — confirmed by repo-wide grep, only `FactionValidatorTests.cs` calls it); godot/src/Core/Sim/ScenarioApplier.cs:276-284 (`GetUnitByCategory("Worker")` returns null → `if (workerDef != null)` silently skips spawning, no error, match starts anyway)
reason: summary: Review Loop 2 deliberately split `FactionValidator` into `Validate` (wired into `FactionDefinition.LoadFromFile`, safe for editor Save self-checks) and `ValidateComplete` (the roster-completeness superset, intentionally NOT wired into `LoadFromFile` because doing so broke the "blank mesh_path = box placeholder = always valid" editor workflow — see Spec Change Log). That split correctly fixed the editor regression, but as a side effect `ValidateComplete` — the check that actually protects "malformed factions are caught before they reach a match" (FR-18/AR-39's stated purpose) — is not invoked anywhere in the match-load path (`ScenarioLoadPhase.ResolveSlotFactionDefs`, `MainScene._Ready`'s client faction load, `MainScene.BuildHeadlessServerSimHost`, `ServerBootstrap.Build`). evidence: confirmed via `grep -rn "FactionValidator\."` across `godot/src` — the only production call site is `FactionDefinition.cs`'s `LoadFromFile` calling `Validate` (not `ValidateComplete`); confirmed via direct trace of `ScenarioApplier.cs`'s worker-spawn logic that a faction missing a Worker-category unit degrades to an unplayable, no-error match start today, for ANY future hand-edited or wizard-authored faction JSON (alpha/beta themselves are fine — hand-verified, always had a Worker). Independently surfaced by the Intent Alignment review layer in Review Loop 3 (Pass 3) as a genuine, non-speculative gap; NOT closed in Story 5.2 itself — deliberately, per epic-5-context.md's own sequencing, which assigns "excludes/flags invalid authored factions" to Story 5.7 and playtest-validation to Story 5.8, and because wiring this into `MainScene.cs`/`ScenarioLoadPhase.cs`/`ServerBootstrap.cs` (multiplayer-determinism-critical, Story-5.1/1.9a surface) for a currently-zero-exploitability risk (no wizard exists yet to author a bad faction) was judged disproportionate scope/regression-risk for this story's automated, unattended dev-auto run.
closure: before or during Story 5.5 (wizard save-gate) / 5.7 (selectability) / 5.8 (playtest validation) — whichever lands first should wire `FactionValidator.ValidateComplete` into the actual match-load gate, following the SAME shadow-mode, non-blocking-by-default idiom already established in this exact file (`ScenarioLoadPhase.ResolveSlotFactionDefs`'s existing `UnitTagValidator.ValidateAndDropUnits` → `GD.PrintErr` diagnostic-only pattern, and `ServerBootstrap.Build`'s `log.Warn` mirror) rather than inventing a new blocking policy. Must be done before any wizard/hand-edit path can author a faction that omits a Worker unit.
status: done 2026-07-15
resolution: already resolved: godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:337 now calls FactionValidator.ValidateComplete(def) as the per-slot match-load shadow diagnostic, and godot/src/Core/Definitions/FactionDefinition.cs:318 gates LoadSelectableFromDirectory on ValidateComplete — exactly the shadow-mode match-load gate the closure prescribed. ValidateComplete is no longer callerless.
resolution: PARTIALLY resolved by Story 5.7 (spec-5-7-wizard-authored-factions-are-immediately-selectable-in-playtest-skirmish-fr-19-ux-dr80.md) — the CLIENT-side half only. (1) discovery half — `FactionDefinition.LoadSelectableFromDirectory` gates every `*_faction.json` through `ValidateComplete` before including it in the selectable set, called fresh every `MainScene._Ready` (client only); live-verified via godot-verify (dropping a new valid/invalid faction file and observing the console's discovered/skipped list on the next Play run). (2) client match-load half — `ScenarioLoadPhase.ResolveSlotFactionDefs` now runs `ValidateComplete` immediately after tag-validation and `GD.PrintErr`s each located error, shadow-mode/non-blocking, mirroring the `UnitTagValidator.ValidateAndDropUnits` idiom in the same method; live-verified via godot-verify (a scenario slot assigned a `ValidateComplete`-failing faction produced the located `GD.PrintErr` diagnostic on boot, match still started, no crash). Covered by `FactionDiscoveryTests.cs` (discovery half, 8 xunit cases) plus the two live-verify passes above; `FactionRegistryTests`/`MultiFactionGoldenTests` were re-run only to confirm NO REGRESSION in unrelated checksum behavior — they do not themselves exercise this diagnostic (correcting an earlier overclaim caught by review). REMAINING OPEN: `MainScene.BuildHeadlessServerSimHost`'s own per-slot `FactionDefinition.LoadFromFile` (`godot/src/Core/MainScene.cs:1484`) and `ServerBootstrap.Build` (the dedicated-server / multiplayer-authoritative match-load path) still call zero `ValidateComplete` check — a faction assigned to a slot on a headless dedicated server gets no roster-completeness diagnostic at all. Deliberately NOT wired by Story 5.7: `ServerBootstrap.Build`/`BuildHeadlessServerSimHost` is the multiplayer-determinism-critical, Story-1.9a surface this entry's own original reasoning already flagged as disproportionate risk for an unattended dev-auto run; Story 5.7 judged the same way for this same call site. Left open for whichever future story next touches the dedicated-server match-load path to close, using the identical shadow-mode `log.Warn` idiom this entry originally named.

### DW-98: The combat/economy unit-category list is now hand-duplicated in a THIRD independent place
source_spec: `_bmad-output/implementation-artifacts/spec-5-2-faction-schema-extension-validator-ar-39-ar-12-fr-18-data.md`
location: godot/src/Core/Definitions/FactionValidator.cs (`_combatCategories = { "Melee", "Ranged", "Siege", "Air" }`), also pre-existing in godot/src/Core/Definitions/UnitDefinitionValidator.cs (`_categories`, all six) and godot/src/CreationSuite/BehaviorRegistry.cs (`_archetypes`, all six)
reason: summary: `FactionValidator`'s required-roles check adds a third independent hardcoded copy of the project's 6-archetype category list (this one a 4-element combat-only subset). evidence: confirmed by grep — no shared constant/enum backs any of the three; a future category rename or addition (e.g. adding a 7th archetype) requires remembering to touch all three files, with no compiler error if one is missed. Flagged by the Blind Hunter review layer, Review Loop 3 (Pass 3). Pre-existing duplication pattern (the first two copies predate this story) that this story's addition makes marginally worse, not introduces.
closure: extract a single `UnitCategory`-adjacent closed-set source of truth (e.g. a `static readonly` array or enum on `UnitDefinition` itself) that `UnitDefinitionValidator`, `BehaviorRegistry`, and `FactionValidator` all reference — a cross-cutting cleanup better done once, not piecemeal per-story.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-unit-category-single-source

### DW-99: No guard prevents a future call site from mistakenly calling `FactionValidator.Validate` where it means `ValidateComplete` (or vice versa)
source_spec: `_bmad-output/implementation-artifacts/spec-5-2-faction-schema-extension-validator-ar-39-ar-12-fr-18-data.md`
location: godot/src/Core/Definitions/FactionValidator.cs (`Validate`/`ValidateComplete`)
reason: summary: the two methods are near-identical in name and signature; only their doc comments (and this deferred-work entry) distinguish "safe for every load, including mid-edit Save" from "only for callers that mean is-this-faction-finished." A future story wiring a new gate could pick the wrong one and either resurrect the Review Loop 2 editor-breaking regression (calling `ValidateComplete` from a lenient path) or resurrect the DW-97 gap (calling `Validate` where `ValidateComplete` was needed), with no compiler or test signal either way. evidence: flagged by the Blind Hunter review layer, Review Loop 3 (Pass 3); no existing analyzer/naming convention in this codebase currently distinguishes "safe everywhere" vs "gate-only" validator methods by type or attribute.
closure: when DW-97 is picked up, consider a stronger signal than a doc comment — e.g. renaming to make the safety contract explicit (`ValidateStructural`/`ValidateReadyToShip`), or a lightweight `[MustBeCalledAtGate]`-style marker/analyzer — evaluate at that time rather than pre-emptively renaming a freshly-shipped, tested API now.
status: done 2026-07-16
resolution: closed by human decision: Avoid pre-emptively renaming a freshly-shipped tested API; revisit only if a real mis-call occurs.
decision: 2026-07-16 Keep doc-comment distinction — Avoid pre-emptively renaming a freshly-shipped tested API; revisit only if a real mis-call occurs.

### DW-100: `TechTreeValidator.Validate` throws `NullReferenceException` on a null element inside a non-null `Units`/`Buildings` list (pre-existing, rediscovered)
source_spec: `_bmad-output/implementation-artifacts/spec-5-2-faction-schema-extension-validator-ar-39-ar-12-fr-18-data.md`
location: godot/src/Core/Definitions/TechTreeValidator.cs:75-77 (`foreach (UnitDefinition u in def.Units) { string id = u.Id ?? ""; foreach (string prereq in u.Prerequisites ?? ...) ... }` — dereferences `u.Prerequisites` before any null check on `u` itself)
reason: summary: an authored `"units": [null, {...}]` (a malformed-but-parseable JSON array containing a null element) crashes `TechTreeValidator.Validate` with an NRE instead of a clean located error, before `FactionValidator`'s own new checks (which DO null-guard their own per-element loops, added this story) ever run — since `TechTreeValidator` runs earlier in `FactionValidator.Validate`'s pipeline. evidence: reproduced directly while hardening `FactionValidator`'s own duplicate-unit-id loop against the same input class (Review Loop 3, Pass 3 patch round) — a test exercising this exact scenario through `FactionValidator.Validate` failed with the NRE originating in `TechTreeValidator.cs:77`, confirming the crash happens upstream of any of this story's own code. Pre-existing (Story 4.2's own code, unchanged by this diff) — out of this story's scope to fix (`TechTreeValidator` is not a file this spec's Boundaries authorize touching).
closure: add the same defensive `if (u is null) continue;` (or a located "a units[] entry is null" error, matching this story's own convention) to `TechTreeValidator.Validate`'s unit-referential-lint loop — a small, isolated fix, best done as its own tightly-scoped patch against Story 4.2's file rather than folded into an unrelated story.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening

### DW-101: `ResourceCostValidator.Validate` throws `NullReferenceException` on a null `Units`/`Buildings` list or a null element inside it (pre-existing sibling to DW-100)
source_spec: `_bmad-output/implementation-artifacts/spec-5-2-faction-schema-extension-validator-ar-39-ar-12-fr-18-data.md`
location: godot/src/Core/Definitions/ResourceCostValidator.cs:54-58 (`foreach (UnitDefinition u in def.Units) ValidateEntry(errors, "unit", u.Id ?? "", u.Cost);` and the identical `def.Buildings` loop — no null-list guard, and `u.Id`/`b.Id` dereferenced with no null-element guard)
reason: summary: the same null-intolerance class as DW-100 but in a DIFFERENT relocated sub-validator that DW-100 does not name. A malformed-but-parseable `"units": null` / `"buildings": null` (null list) or `"units": [null, {...}]` (null element) NREs inside `ResourceCostValidator.Validate` just as it does in `TechTreeValidator.Validate`. Discovered while verifying DW-100 during this story's follow-up review (Pass 4): reading `ResourceCostValidator.cs` directly confirmed lines 54-58 iterate and dereference with no guard, identical to `TechTreeValidator`. evidence: `ResourceCostValidator.Validate` (Story 4.3's own code, unchanged by this diff) is called by `FactionValidator.Validate` immediately after `TechTreeValidator.Validate`, so in the FactionValidator pipeline `TechTreeValidator` (DW-100) NREs first and masks this one — but `ResourceCostValidator` is `public` and independently reachable (direct/future callers), so the flaw is real on its own. NOTE: this story's Pass-4 patch added a structural null pre-check at the TOP of `FactionValidator.Validate` (null list / null element caught and early-returned BEFORE delegating), so the `FactionValidator` path itself no longer NREs on these inputs; DW-100 and DW-101 now concern only the sub-validators' own robustness for their other/direct callers. Out of this story's Boundaries to fix directly (`ResourceCostValidator` is not a file this spec authorizes touching).
closure: add the same defensive null-list + `if (u is null) continue;` guards to `ResourceCostValidator.Validate`'s two loops — pairs naturally with DW-100's `TechTreeValidator` fix; both are small, isolated patches best landed together against the Story 4.x validator files rather than folded into an unrelated story.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening

### DW-102: The `aviary` building's `mesh_path` in both `alpha_faction.json`/`beta_faction.json` (`bonded_aerie.glb`/`wraithwing_brood.glb`) does not resolve to an on-disk GLB
source_spec: `_bmad-output/implementation-artifacts/spec-5-3-land-harden-the-fma-showcase-content-as-valid-definer-outputs-fr-20.md`
location: `godot/resources/data/factions/alpha_faction.json` (`buildings[].id=="aviary"`, `mesh_path: "res://assets/models/factions/alpha/bonded_aerie.glb"`) and `godot/resources/data/factions/beta_faction.json` (`buildings[].id=="aviary"`, `mesh_path: "res://assets/models/factions/beta/wraithwing_brood.glb"`) — neither GLB exists under `godot/assets/models/factions/{alpha|beta}/`.
reason: summary: both `aviary` buildings (an Air-production building + category, Story 2.8, predating and unrelated to the `c495454` corrupting reinstall commit this story fixes) reference meshes that were never part of the 24-asset FMA manifest this story's showcase content was drawn from, and so were never generated by Alec's local art-gen pipeline. This story's boundaries explicitly exclude touching the `aviary` entries (removing a working, cost/prereq-gated build-menu button would regress it to a phantom free-build fallback) and exclude any new asset generation (out of this session's tooling). evidence: `find godot/assets/models/factions/{alpha,beta} -iname "bonded_aerie.glb" -o -iname "wraithwing_brood.glb"` returns no matches; both JSONs' other 8 units + 4 non-aviary buildings (the FMA-manifest scope) all resolve to on-disk GLBs post this story's edits.
closure: generate (or source) `bonded_aerie.glb`/`wraithwing_brood.glb` via Alec's local art-gen pipeline and drop them into `godot/assets/models/factions/{alpha|beta}/` respectively — a pure asset-authoring task, no code or schema change needed; the JSON `mesh_path` values are already correct and will resolve as soon as the files exist.
status: open
decision: 2026-07-15 Repoint to placeholder GLB — Edit alpha_faction.json and beta_faction.json aviary.mesh_path to an existing on-disk placeholder GLB so ValidateComplete + scenario load succeed until real art lands.
decision: 2026-07-15 Repoint to placeholder GLB — Edit alpha_faction.json and beta_faction.json aviary.mesh_path to an existing on-disk placeholder GLB so ValidateComplete + scenario load succeed until real art lands.

### DW-103: `FactionDefinition.GetUnit`/`IndexOfUnit`/`GetUnitByCategory`/`GetUnitsByCategory` NRE on a null element inside a non-null `Units` list (pre-existing, sibling to DW-100/DW-101)
source_spec: `_bmad-output/implementation-artifacts/spec-5-3-land-harden-the-fma-showcase-content-as-valid-definer-outputs-fr-20.md`
location: `godot/src/Core/Definitions/FactionDefinition.cs:86-91` (`GetUnit`), `:124-129` (`IndexOfUnit`), `:132-155` (`GetUnitByCategory`/`GetUnitsByCategory`) — each `foreach (var u in Units)` dereferences `u.Id`/`u.Category` with no null-element guard, unlike `GetResearch`/`IndexOfResearch` on the same class, which already skip a null element.
reason: summary: the same null-intolerance class as DW-100/DW-101 but in a THIRD, different set of methods neither entry names. A `FactionDefinition` built by any path that doesn't run `FactionValidator.Validate`'s structural pre-check first (e.g. direct `JsonSerializer.Deserialize`, or a hand-built definition in a test/tool) and whose `Units` list contains a null element would NRE on `GetUnit`/`IndexOfUnit`/`GetUnitByCategory`/`GetUnitsByCategory` instead of skipping it. evidence: surfaced by the Edge Case Hunter review layer on Story 5.3's diff, which added `GetUnit("griffin")`/`GetUnit("fatso")` calls in `FactionValidatorTests.cs` — those calls are safe today only because `alpha_faction.json`'s `Units` list, loaded through `LoadFromFile` -> `FactionValidator.Validate`'s structural pre-check, is guaranteed non-null-element by the time `GetUnit` runs; a direct caller bypassing that gate is not protected. Not caused by Story 5.3 (the methods are unchanged, pre-existing since before Story 5.2).
closure: add the same defensive `if (u is null) continue;` guard used in `GetResearch`/`IndexOfResearch` to `GetUnit`/`IndexOfUnit`/`GetUnitByCategory`/`GetUnitsByCategory`'s loops — a small, isolated patch best landed alongside DW-100/DW-101's `TechTreeValidator`/`ResourceCostValidator` fixes rather than folded into an unrelated story.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening

### DW-104: No validator or test in the codebase checks that an authored `mesh_path` resolves to an actual on-disk file (systemic gap, pre-existing since Story 5.2)
source_spec: `_bmad-output/implementation-artifacts/spec-5-3-land-harden-the-fma-showcase-content-as-valid-definer-outputs-fr-20.md`
location: `godot/src/Core/Definitions/FactionValidator.cs:203-219` (`ValidateComplete`'s mesh_path check uses `string.IsNullOrWhiteSpace` only, never `File.Exists`/`ResourceLoader.Exists`).
reason: summary: `FactionValidator.ValidateComplete` (Story 5.2) — the one validator this project treats as the "is this faction genuinely complete/playable" gate — only checks a `mesh_path` field is non-blank, never that it resolves to a real file on disk. DW-102 (this same story) is a concrete instance the gap allowed to ship silently (the `aviary` buildings' dangling `mesh_path` values pass `ValidateComplete` today). Three independent review layers (Blind Hunter, Edge Case Hunter, Intent Alignment) converged on this same observation during Story 5.3's review. evidence: read `FactionValidator.cs`'s `ValidateComplete` mesh_path loop directly; confirmed the `aviary` buildings' `bonded_aerie.glb`/`wraithwing_brood.glb` (non-existent on disk) pass `ValidateComplete().Ok` unchanged. Not caused by Story 5.3 — the check has worked this way since Story 5.2 authored it.
closure: add a `File.Exists`/`ResourceLoader.Exists`-backed disk-existence check to `ValidateComplete`'s mesh_path loop (or a new, explicitly-named third method, given DW-99's existing concern about `Validate`/`ValidateComplete` ambiguity) — evaluate whether this belongs in the sim-layer validator at all (it reads the filesystem, a presentation-layer concern) versus a separate content-authoring lint step, before implementing.
status: open
decision: 2026-07-25 Separate content-lint step — Add the disk-existence check in a presentation-layer/editor Save-edge content lint, keeping the sim validator filesystem-free (2026-07-19 decision)
decision: 2026-07-25 Separate content-lint step — Add the disk-existence check in a presentation-layer/editor Save-edge content lint, keeping the sim validator filesystem-free (2026-07-19 decision)
decision: 2026-07-19 Separate content-lint step — Add the disk-existence check in a presentation-layer/content-authoring lint (e.g. at the editor Save/discovery edge) rather than the pure sim validator.
decision: 2026-07-16 Add File.Exists to ValidateComplete — Add a File.Exists/ResourceLoader.Exists-backed check to ValidateComplete's mesh_path loop with a located error.

### DW-105: `_bmad-output/fma-faction-design.md`'s Air-unit narrative is stale relative to the shipped Story 2.8 Aviary building
source_spec: `_bmad-output/implementation-artifacts/spec-5-3-land-harden-the-fma-showcase-content-as-valid-definer-outputs-fr-20.md`
location: `_bmad-output/fma-faction-design.md:122,150,167,183` — repeatedly states "no Air production building exists," both air units (griffin, wyvern) are "unbuildable except via scenario placement," and lists "Air production building + Air category mapping" as an open, not-yet-built needs-new-code epic.
reason: summary: this narrative was accurate when the doc was authored (2026-06-21) but Story 2.8 (shipped ~2026-07-01, per its epics.md AC "Given an Air production building placed... the Air unit appears as a trainable option and trains correctly") built exactly this: a real `BuildingType.Aviary` mapped to the `"Air"` category, already wired into both `alpha_faction.json`/`beta_faction.json` as a cost/prereq-gated buildable building before this story touched either file. A future reader of the design doc (or an agent planning a later Epic 5 story) could be misled into re-litigating "should we build an Air producer" when it already shipped. Flagged by the Blind Hunter review layer during Story 5.3's review. Not caused by Story 5.3 (the doc predates and is untouched by this story; touching a planning-phase design doc is outside this story's data-only boundaries).
closure: add a dated addendum note near the top of `fma-faction-design.md` (or its own "Open Decisions" section) recording that Decision #1 ("AIR THIS MILESTONE?") was resolved YES by Story 2.8, and that the Air-production-building epic in the "Needs-new-code epics" table has since shipped — a documentation-only fix, no code/data change.
status: open

### DW-106: `FactionValidator` never resolves `signature_mechanic_effect_id` against the `AbilityRegistry` for any faction (systemic gap, pre-existing since Story 5.2)
source_spec: `_bmad-output/implementation-artifacts/spec-5-4-wire-the-two-signature-mechanics-via-d1-modifiers-fr-20-unique-mechanic.md`
location: `godot/src/Core/Definitions/FactionValidator.cs` — `Validate`/`ValidateComplete` never reference `SignatureMechanicEffectId` at all; the only checks touching it live in `FactionValidatorTests.cs` (non-empty-string assertions).
reason: summary: this is exactly the class of gap that let alpha's `signature_mechanic_effect_id` silently drift to `"equal_exchange_self_cost"` — a string matching no real ability — through every existing gate (schema validation, faction tests, content review) until Story 5.4's investigation caught it by direct source inspection. Fixing alpha's one value and adding a test-level resolution check (this story) closes the SYMPTOM for both shipped factions; it does not close the systemic gap for any future/creator-authored faction, since `FactionValidator` itself still performs no such cross-check at load time. evidence: read `FactionValidator.cs` in full — zero references to `SignatureMechanicEffectId`/`SignatureMechanicId`; confirmed via `grep` that no production code resolves this field against `AbilityRegistry` anywhere in the repo (converged finding, Blind Hunter + Verification Gap Reviewer, Story 5.4 review pass 1).
closure: add a `Validated<T>`-style check to `FactionValidator.Validate`/`ValidateComplete` that, given an `AbilityRegistry`, confirms a non-empty `signature_mechanic_effect_id` resolves to a real loaded ability id — located error naming the faction and the dangling id. Likely belongs alongside Story 5.5/5.6's wizard save-gate work (the validator is reused there), since it needs a registry reference the validator doesn't currently take as a parameter.
status: done 2026-07-16
resolution: already resolved: FactionValidator.cs:294-297 ValidateComplete now resolves signature_mechanic_effect_id via abilityRegistry.IndexOf; hero check at 286-289; fixed in commit f6a78bd (story 14-3-remediation-dw-106)

### DW-107: `AbilityRegistry.LoadFromDirectory`'s silent-skip-on-invalid-file behavior is unguarded in the new real-content signature-mechanic tests
source_spec: `_bmad-output/implementation-artifacts/spec-5-4-wire-the-two-signature-mechanics-via-d1-modifiers-fr-20-unique-mechanic.md`
location: `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (`LoadRealContent`'s call to `AbilityRegistry.LoadFromDirectory`, `onSkipped` left at its default `null`).
reason: summary: `AbilityRegistry.LoadFromDirectory` silently excludes any ability file that fails `AbilityValidator` (via an optional `onSkipped` callback nobody wires here). None of the new "real content" tests assert `registry.Count`/wire `onSkipped` to fail loudly, so a future ability JSON edit that breaks validation (e.g. a bad edit to `furnace_pour.json`) would silently shrink the registry and these tests would not notice — undercutting the class's own stated selling point of testing against genuinely shipped content. evidence: read `AbilityRegistry.LoadFromDirectory`'s signature and skip-handling directly (Blind Hunter, Story 5.4 review pass 1).
closure: either assert `registry.Count` against a known expected minimum in `LoadRealContent`, or pass an `onSkipped` callback that fails the test (e.g. `Assert.Fail($"ability file skipped: {path}")`) — a small, isolated test-hardening patch, not urgent given no ability file currently fails validation.
status: open

### DW-108: Fixed-magnitude pre-damage pattern in `SignatureMechanicRealContentTests` is fragile against future low-HP roster content
source_spec: `_bmad-output/implementation-artifacts/spec-5-4-wire-the-two-signature-mechanics-via-d1-modifiers-fr-20-unique-mechanic.md`
location: `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (`world.Health[betaUnit] -= Fixed.FromInt(30)` and the alpha equivalent, in both `SanguineFurnace_RealBetaUnit_Regenerates_RealAlphaUnit_DoesNot` and `BuildCombinedScenarioHarness`).
reason: summary: the fixed `-30` pre-damage amount is safe today (`forgehand` hp 80, `worker` hp 55, both well above 30) but is not derived from either unit's actual `Hp`, so a future roster edit dropping either unit's HP to ≤30 would silently drive it to zero/negative before the regen loop runs, invalidating the isolation being tested — not a defect in the shipped content today, purely a latent fragility in the test. evidence: Edge Case Hunter, Story 5.4 review pass 1.
closure: derive the pre-damage amount as a fraction of each unit's real `Hp` (e.g. `Fixed.FromFloat(def.Hp) / 4`) instead of a fixed magic number, or add an `Assert.True(def.Hp > 30, ...)` guard before subtracting — a small, isolated test-hardening patch.
status: open

### DW-109: `SignatureMechanicScenario_TwoRuns_ByteIdenticalChecksums` re-reads real content from disk twice per test run
source_spec: `_bmad-output/implementation-artifacts/spec-5-4-wire-the-two-signature-mechanics-via-d1-modifiers-fr-20-unique-mechanic.md`
location: `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (`SignatureMechanicScenario_TwoRuns_ByteIdenticalChecksums` calls `BuildCombinedScenarioHarness` — which calls `LoadRealContent`, hitting the filesystem via `AbilityRegistry.LoadFromDirectory` + two `FactionDefinition.LoadFromFile` calls — twice, once per run).
reason: summary: this makes the determinism proof partially a proof that "the filesystem returns the same bytes twice in the same process," a weaker and slower claim than the in-memory two-run pattern most other golden scenarios use (e.g. `EqualExchangeScenario`/`GoldenScenario`, which build fixtures purely in code). Not a correctness bug — the test passed and ran in ~150ms — but a hermeticity/perf cost worth noting if this pattern is reused for larger scenarios. evidence: Blind Hunter, Story 5.4 review pass 1.
closure: if this pattern is extended to larger/slower scenarios in a future story, consider loading real content once into shared static fixtures at class-load time rather than per-run, mirroring how `CanonicalScenarioTests` amortizes its own real-content loads. Not worth doing for this story's small, fast scenario alone.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Test-hermeticity/perf observation; the entry's own closure states it is 'not worth doing for this story's small, fast scenario alone' and is conditioned on a future extension to larger/slower scenarios that has not occurred.

### DW-110: `BuildCombinedScenarioHarness` issues cast intents inside the `build` callback rather than via `GoldenChecksumReplay.RunAndRecord`'s `perturb` hook
source_spec: `_bmad-output/implementation-artifacts/spec-5-4-wire-the-two-signature-mechanics-via-d1-modifiers-fr-20-unique-mechanic.md`
location: `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (`BuildCombinedScenarioHarness`'s `IssueSelfCast` calls, issued once per fresh `build()` invocation rather than through `RunAndRecord`'s per-iteration `perturb` parameter).
reason: summary: this makes the "cast fires the same tick as the first `StepOnce`" invariant depend on `GoldenChecksumReplay.RunAndRecord`'s current calling convention (fresh `build()` per run, no re-issuance mid-run) rather than an explicit, harness-enforced contract — a future refactor of `RunAndRecord`'s semantics (e.g. rebuilding and reissuing per iteration) could silently break this test's assumption without a compiler error. Speculative; the harness's contract has not changed since it was introduced. evidence: Blind Hunter, Story 5.4 review pass 1.
closure: no action needed unless `GoldenChecksumReplay.RunAndRecord`'s calling convention changes; if it does, audit every test using the `build`-callback-issues-intents pattern (not unique to this story) for the same assumption.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Speculative no-op note: only meaningful 'if GoldenChecksumReplay.RunAndRecord's calling convention changes' — it has not changed, so the entry remains an inert note.

### DW-111: `FactionValidator` checks duplicate `Units` ids but never duplicate `Buildings`/`Research` ids (pre-existing gap, pre-dates Story 5.5)
source_spec: `_bmad-output/implementation-artifacts/spec-5-5-faction-definer-guided-wizard-flow-validator-gated-save-fr-17-ux-dr40.md`
location: `godot/src/Core/Definitions/FactionValidator.cs:160-179` — the duplicate-id check (`unitIds.TryAdd(u.Id, u)`) exists only in the `Units` loop; no equivalent `buildingIds`/`researchIds` `TryAdd` check exists anywhere in `Validate`/`ValidateComplete`.
reason: summary: Story 5.5's Faction Definer wizard makes it trivial to pick buildings/research from two different scanned source factions (alpha + beta) in the same session; if either ever ships a building/research entry sharing an id with the other, the wizard would happily assemble and write a faction JSON with two same-id building/research entries with no located error, silently. `FactionValidator` itself (Story 5.2) is the root cause and is unchanged by Story 5.5 — the wizard only calls it. Surfaced by the Blind Hunter review layer on Story 5.5's diff; confirmed directly by reading `FactionValidator.cs` (grep for "duplicate"/"TryAdd" returns only the `Units` occurrence).
closure: add a `buildingIds`/`researchIds` `TryAdd`-based duplicate check mirroring the existing `unitIds` one, in the same relocated-checks section of `FactionValidator.Validate`/`ValidateComplete`. Small, isolated fix; not urgent since today's two shipped factions (alpha/beta) have no colliding building/research ids.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening

### DW-112: `FactionDefinerWizardCore.TryFinish` has a TOCTOU race between its `File.Exists` guard and the later `File.Move(overwrite:false)`
source_spec: `_bmad-output/implementation-artifacts/spec-5-5-faction-definer-guided-wizard-flow-validator-gated-save-fr-17-ux-dr40.md`
location: `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` (`TryFinish`'s `File.Exists(targetAbs)` check, followed later by `File.Move(tmp, targetAbs, overwrite: false)`).
reason: summary: if another process (or a second wizard session) creates `targetAbs` in the window between the exists-check and the move, `File.Move` throws and the generic `catch (Exception ex)` branch reports a raw `"save failed: {ex.Message}"` on the `id` field instead of the friendlier "already exists, choose a different id" message the same collision produces when caught by the earlier check — a UX inconsistency, not a data-loss risk (the atomic move itself still never overwrites). Surfaced independently by both the Blind Hunter and Edge Case Hunter review layers on Story 5.5's diff; low probability given this is a single local Godot editor session, not a multi-process/networked write path.
closure: catch the specific `IOException`/`UnauthorizedAccessException` from the `File.Move` call, check `File.Exists(targetAbs)` again, and return the same "already exists" located error in that case instead of falling through to the generic `"save failed"` message. Small, isolated patch; not urgent given the single-session usage pattern.
status: open

### DW-113: `FactionDefinerPanel.Toggle()`/`ResetWizard()` discards in-progress wizard state with no confirmation on re-open
source_spec: `_bmad-output/implementation-artifacts/spec-5-5-faction-definer-guided-wizard-flow-validator-gated-save-fr-17-ux-dr40.md`
location: `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` (`ResetWizard`, called unconditionally by `Toggle()` on every open) — no "discard changes?" prompt.
reason: summary: since `Key.X` both opens and (per the established sibling-panel `Toggle()` pattern) closes the panel, an accidental second `X` press while mid-edit re-opens a freshly-reset wizard, silently discarding all picks made so far — by design per this story's own Design Notes ("the wizard never carries partial state across a close"), but a real creator-facing usability gap all the same. Surfaced by the Blind Hunter review layer on Story 5.5's diff. Not this story's stated requirement to fix (the spec's Design Notes explicitly establish the no-partial-state-across-close behavior as intended), so left as a UX improvement for a future pass rather than reworked here.
closure: if this friction proves real in practice, add a lightweight "discard unsaved wizard progress?" confirmation before `ResetWizard()` runs on a re-open that finds non-default draft state (e.g. any `_draft.Units`/`Buildings`/`Research` non-empty, or `Id`/`DisplayName` non-blank). Not urgent; no user complaint yet.
status: open
decision: 2026-07-20 Add discard confirmation — Show a 'discard unsaved wizard progress?' confirmation before ResetWizard() runs on a re-open that finds non-default draft state.
decision: 2026-07-16 Add discard confirmation — Show a 'discard unsaved wizard progress?' confirmation before ResetWizard() runs on a re-open that finds non-default draft state.

### DW-114: `FactionDefinerWizardCore.StepForError` has no case for `signature_mechanic*`/`hero_unit_id`/`starting_ore`/`starting_crystal` field paths (currently unreachable — latent trap if `FactionValidator` is ever extended to check them)
source_spec: `_bmad-output/implementation-artifacts/spec-5-5-faction-definer-guided-wizard-flow-validator-gated-save-fr-17-ux-dr40.md`
location: `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` (`StepForError`'s `switch (fieldPath)` — only `color`/`id`/`ai_preset`/`units` are named; everything else falls through to the unit/building message-prefix sniff, defaulting to `BuildingsTech`).
reason: summary: confirmed by grepping `FactionValidator.cs` that `Validate`/`ValidateComplete` never currently emit an error with field path `signature_mechanic`/`signature_mechanic_effect_id`/`hero_unit_id`/`starting_ore`/`starting_crystal` — so this is dead code today, not a live bug. But if a future story (the DW-106 closure, or bounds-checking DW-115 below) adds such a check, the resulting located error would silently misroute to the `BuildingsTech` step, which has no UI for any of those fields, since the wizard's 5 steps never expose them for editing at all (Roster/Buildings & Tech, Starting Conditions only shows the two new float fields, AI Preset is a non-interactive stub). Surfaced independently by both the Blind Hunter and Edge Case Hunter review layers on Story 5.5's diff.
closure: whenever `FactionValidator` is extended to validate `signature_mechanic*`/`hero_unit_id`/`starting_ore`/`starting_crystal` (see DW-106, DW-115), add matching `StepForError` cases at the same time — likely routing to `StartingConditions` for the two new float fields and `AiPreset`/a new step for the mechanic/hero fields, since neither is currently editable from any wizard step.
status: open

### DW-115: `FactionValidator` has no bounds check for negative `FactionDefinition.StartingOre`/`StartingCrystal`
source_spec: `_bmad-output/implementation-artifacts/spec-5-5-faction-definer-guided-wizard-flow-validator-gated-save-fr-17-ux-dr40.md`
location: `godot/src/Core/Definitions/FactionValidator.cs` (`Validate`/`ValidateComplete` never reference `StartingOre`/`StartingCrystal`); `godot/src/Core/Definitions/FactionDefinition.cs` (the two new fields, plain `float`, no range constraint at the schema level).
reason: summary: a hand-edited faction JSON (bypassing the wizard, whose `NumInput` UI clamps to `[0, 100000]`) with a negative `starting_ore`/`starting_crystal` passes `ValidateComplete().Ok` unchanged and would be written/loaded without complaint. Not caused by Story 5.5 — this is the same "the validator doesn't check X" class of gap as DW-104/DW-106, now covering the two fields this story introduces. Surfaced by the Edge Case Hunter review layer on Story 5.5's diff; confirmed directly (grep of `FactionValidator.cs` for `StartingOre`/`StartingCrystal`/`starting_ore`/`starting_crystal` returns no matches).
closure: add a `>= 0` check for both fields to `FactionValidator.Validate`/`ValidateComplete`, located to the faction id — small, isolated patch, likely worth batching with DW-104/DW-106/DW-111's other `FactionValidator` gaps rather than a one-off.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-content-validator-hardening

## From code review of story-5.6 (2026-07-11)

### DW-116: `FactionDefinerWizardCore.StepForError` has no case for the new `raw_json` field path (currently unreachable — falls through to `BuildingsTech`)
source_spec: `_bmad-output/implementation-artifacts/spec-5-6-ai-preset-selection-advanced-raw-json-mode-hero-persistence-config-completion-target-fr-18-ux-dr80-ar-12.md`
location: `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` (`StepForError`'s `switch (fieldPath)` — no `"raw_json"` case; a `TryFinishFromRawJson` parse-failure error's field path falls through the sniff logic to the `BuildingsTech` default).
reason: summary: harmless today only because `OnFinishPressed` special-cases `!_advancedMode` before ever reading `result.Step` (Advanced mode has no step tabs to jump to), so the wrong mapping is never observed. Surfaced independently by Blind Hunter, Edge Case Hunter, and Verification Gap reviewers across both review passes on this story's diff — a slightly misleading default that would mislead any future caller of `TryFinishFromRawJson` that does consume `Step` (logging, a step-chip on the error label, a different UI surface).
closure: add `case "raw_json": return FactionDefinerStep.NameColor;` (or another defensible default) with a comment explaining `Step` is unused in Advanced mode today — small, isolated, zero behavioral risk since no current caller reads it.
status: open

### DW-117: Advanced-mode raw JSON that omits the `ai_preset` key silently inherits `FactionDefinition`'s C# `"balanced"` default, bypassing the explicit-choice enforcement Simple mode has via `ResetWizard`'s `_draft.AiPreset = ""` override
source_spec: `_bmad-output/implementation-artifacts/spec-5-6-ai-preset-selection-advanced-raw-json-mode-hero-persistence-config-completion-target-fr-18-ux-dr80-ar-12.md`
location: `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` (`TryFinishFromRawJson`'s `JsonSerializer.Deserialize<FactionDefinition>` — a JSON document that omits `ai_preset` entirely leaves the class's own `"balanced"` default untouched, unlike the Simple-mode draft which explicitly starts at `""`).
reason: summary: `"balanced"` is a valid, sane closed-set member, so this never produces corrupt/dangling data — but it is a real asymmetry between the two modes' "must explicitly author `ai_preset`" guarantee (Simple blocks an unauthored preset; Advanced can silently accept one via key omission). Surfaced by Edge Case Hunter on review pass 2. Low priority: reachable only by a creator deliberately omitting a key in the raw-JSON escape hatch, an unusual and low-consequence action.
closure: if symmetry is later required, have `TryFinishFromRawJson` (or a pre-parse step) distinguish "key present and empty" from "key absent" — e.g. parse via `JsonNode` first and require an explicit `ai_preset` key before falling back to the POCO deserialize — and treat an absent key the same as Simple mode's forced `""`.
status: done 2026-07-16
resolution: already resolved: FactionDefinerWizardCore.cs:259-267 TryFinishFromRawJson now re-inspects via JsonDocument and forces AiPreset="" when the ai_preset key is absent, flowing through the same 'must be authored' rejection; fixed in commit 766ca42 (story 14-2-remediation-dw-117)

### DW-118: `ClearStaleHeroReference`'s cleared-a-stale-reference signal is discarded by its callers — a creator gets no explanation when their hero pick silently disappears
source_spec: `_bmad-output/implementation-artifacts/spec-5-6-ai-preset-selection-advanced-raw-json-mode-hero-persistence-config-completion-target-fr-18-ux-dr80-ar-12.md`
location: `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` (`TryFinish` calls `ClearStaleHeroReference(def)` and ignores its `bool` return); `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` (`BuildAiPresetStep`'s identical call, also ignored).
reason: summary: the data-integrity guarantee (no dangling `hero_unit_id` ever reaches a written file) is intact and directly tested, but a creator who picks a hero, unpicks that unit via Back → Roster → uncheck, then clicks Finish without revisiting the AI Preset step gets zero explanation for why `hero_unit_id` came back `null` — including, per Blind Hunter's finding, if `ValidateComplete` then blocks on an UNRELATED issue (e.g. missing `mesh_path`), the hero pick is already silently gone by the time the creator sees any error and returns to fix it. Surfaced by Edge Case Hunter (return-value-discarded) and Blind Hunter (silent-mutation-on-failed-attempt) independently on review pass 2.
closure: surface the cleared-reference case to the creator — e.g. `OnFinishPressed` checks `ClearStaleHeroReference(_draft)`'s return before calling `TryFinish` and, if true, shows a status note ("hero selection was cleared — the picked unit is no longer in your roster") before proceeding. Needs a small design decision (block Finish to force acknowledgment, vs. just inform and proceed) — not urgent since the written data is always correct either way.
status: open
decision: 2026-07-20 Inform and proceed — Have OnFinishPressed check ClearStaleHeroReference's return and show a status note ('hero selection was cleared') before proceeding.
decision: 2026-07-16 Inform and proceed — Have OnFinishPressed check ClearStaleHeroReference's return and show a status note ('hero selection was cleared') before proceeding.

### DW-119: Two roster-picked units sharing the same `Id` across preset source files render as indistinguishable duplicate Hero Unit buttons
source_spec: `_bmad-output/implementation-artifacts/spec-5-6-ai-preset-selection-advanced-raw-json-mode-hero-persistence-config-completion-target-fr-18-ux-dr80-ar-12.md`
location: `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` (`BuildAiPresetStep`'s `heroCandidates` button row — one button per hero-flagged unit in `_draft.Units`, no dedup by `Id`).
reason: summary: if two picked units happen to share an `Id` (possible across the alpha/beta preset pools scanned by `ScanPresets` — e.g. both ship an id `command_center`, though not currently as hero-flagged units) and both are hero-flagged, two visually identical buttons alias the same `HeroUnitId` string with no UI signal they're the same underlying pick. `FactionValidator.Validate`'s existing duplicate-unit-id check would independently block Finish on such a roster anyway (a genuine id collision is already invalid), so this is purely a rendering/cosmetic ambiguity in an already-blocked state, not a reachable data-integrity gap. Surfaced by Blind Hunter and Edge Case Hunter independently across both review passes; not currently reproducible with shipped content (neither alpha nor beta has a hero-flagged unit at all).
closure: dedupe `heroCandidates` by `Id` before rendering (`.DistinctBy(u => u.Id)` or equivalent) if this ever becomes reachable — low priority given the validator already blocks the only roster state that would trigger it.
status: open

## Deferred from: review of spec-5-7-wizard-authored-factions-are-immediately-selectable-in-playtest-skirmish-fr-19-ux-dr80 (2026-07-11)

### DW-120: The ACTIVE faction count (`new FactionRegistry(2)`) is still hardcoded to 2 in BOTH boot paths, even though per-slot ASSIGNMENT already supports Player1-4
source_spec: `_bmad-output/implementation-artifacts/spec-5-7-wizard-authored-factions-are-immediately-selectable-in-playtest-skirmish-fr-19-ux-dr80.md`
location: `godot/src/Core/MainScene.cs` (`_Ready`'s `var factions = new FactionRegistry(2);`, client) AND `MainScene.BuildHeadlessServerSimHost`'s own `var factions = new FactionRegistry(2);` + its `ServerBootstrap.Build(..., activeFactionCount: 2, ...)` call (dedicated server — independently hardcoded, not shared with the client path); `SimChecksum`'s per-faction ore/checksum loop iterates exactly `FactionRegistry.ActiveFactions`, which both call sites cap at 2.
reason: summary: this story adds a directory-scan `LoadSelectableFromDirectory` (discovery) and a per-slot `ValidateComplete` shadow diagnostic (match-load), both of which are already Player1-4-aware (proven by `Golden/FactionRegistryTests.cs`'s existing `GetSlotDefinition`/`ToFaction` coverage) — but the ACTIVE, economy-ticked/checksummed faction count remains hardcoded to 2 in TWO independent boot sequences (client `_Ready` and the dedicated-server `BuildHeadlessServerSimHost`/`ServerBootstrap.Build` pair — confirmed by direct read, these do not share one code path). There is no live surface yet (no 3-4 player skirmish/lobby setup screen exists — that is Story 11.1, which itself depends on this story's registry list) that could assign a wizard-authored faction to Player3/4 and expect its economy to actually tick, so widening `ActiveFactions` today would be an unexercised, determinism-critical change with no test surface to prove it correct, in EITHER boot path. Deliberately NOT touched by this story per its own Boundaries & Constraints ("Never" section) — matches this project's established precedent for deferring exactly this class of currently-unexercised risk (the same reasoning DW-97 itself used before this story closed its client-side half). Independently confirmed by 2 review layers (Blind Hunter, Edge Case Hunter) during this story's own review pass, who caught that the first-drafted version of this entry named only the client-side hardcode.
closure: widen BOTH `new FactionRegistry(2)` call sites (and the `activeFactionCount: 2` argument to `ServerBootstrap.Build`) to derive the active count from the loaded scenario's actually-assigned slots (or from a future skirmish/lobby setup screen's player-count selection) once Story 11.1 (or 9.2, which owns `FactionRegistry.SLOT_DEFINITIONS_SIZE`/`PLAYER_COUNT` widening) builds a real surface to exercise a 3-4 player match. Must keep `SimChecksum`'s per-faction loop byte-identical for the existing 2-player case, on BOTH the client and dedicated-server paths, when this lands.
status: done 2026-07-26
resolution: already resolved: Story 9.7 closed both boot paths: client _Ready derives the active count (MainScene.cs:377-378 activePlayers=ClampActivePlayers(PeekScenarioPlayerSlots(...)) -> new FactionRegistry(activePlayers)); dedicated server passes ClampActivePlayers(model.PlayerSlots.Length) as activeFactionCount to ServerBootstrap.Build (MainScene.cs:2263-2269, ServerBootstrap.cs:69); both route through PlayerCountPolicy.SimActivePlayers. The remaining new FactionRegistry(2) at MainScene.cs:2227 is slot-storage only and explicitly not threaded into the checksum registry.

### DW-121: `FactionDefinition.LoadSelectableFromDirectory`'s discovered factions are not ability-resolved or tag-validated — a latent trap for whichever story next consumes this list to assign a match slot
source_spec: `_bmad-output/implementation-artifacts/spec-5-7-wizard-authored-factions-are-immediately-selectable-in-playtest-skirmish-fr-19-ux-dr80.md`
location: `godot/src/Core/Definitions/FactionDefinition.cs` (`LoadSelectableFromDirectory`) vs. `godot/src/Core/MainScene.cs`'s `_factionDef`/`_factionDef2` seeding, which additionally calls `u.ResolveAbilities(_abilityRegistry)` and `UnitTagValidator.ValidateAndDropUnits` before those defs are ever used for spawning.
reason: summary: `LoadSelectableFromDirectory` returns raw, freshly-deserialized `FactionDefinition` objects straight from `JsonSerializer.Deserialize` + `FactionValidator.ValidateComplete` — it does neither ability-id resolution nor unit-tag validation, unlike every OTHER faction def actually used to spawn units in this codebase (`MainScene._Ready`'s `_factionDef`/`_factionDef2`, and `ScenarioLoadPhase.ResolveSlotFactionDefs`'s per-slot defs, both call `ResolveAbilities`/`ValidateAndDropUnits` before the def is considered spawn-ready). Today this is harmless — the discovered list is only ever console-printed, never assigned to a slot or spawned. But it is a latent trap for Story 11.1 (the future skirmish/lobby setup screen), which is the story epics.md itself says will consume this exact discovery list to let a human assign a faction to a player slot — if 11.1's author naively assigns a `LoadSelectableFromDirectory` result straight into `SlotFactionDefs` without separately resolving abilities/tags first, that slot's units would spawn with unresolved `AbilityIndices` and un-dropped unknown-tag units, silently diverging from every other faction-load path in the codebase. Surfaced by the Blind Hunter review layer during this story's own review pass.
closure: when Story 11.1 (or whichever story first assigns a `LoadSelectableFromDirectory` result to a real match slot) wires this up, it must additionally call `ResolveAbilities`/`UnitTagValidator.ValidateAndDropUnits` on the chosen def before assignment — mirroring `MainScene._Ready`'s existing seeding order — or `LoadSelectableFromDirectory` itself should be extended to accept an `AbilityRegistry` and do this internally. Not done by Story 5.7 itself since the discovered list has no live consumer yet to get this order wrong against.
status: open
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 11.1 (skirmish setup screen)

### DW-122: Four near-identical "scan a directory, try/catch-deserialize each file, report skips via callback" loader methods now exist, with no shared helper
source_spec: `_bmad-output/implementation-artifacts/spec-5-7-wizard-authored-factions-are-immediately-selectable-in-playtest-skirmish-fr-19-ux-dr80.md`
location: `godot/src/Core/Definitions/AbilityRegistry.cs` (`LoadFromDirectory`), `BehaviorRegistry.LoadFromDirectory`, `ItemRegistry.LoadFromDirectory`, and now `FactionDefinition.LoadSelectableFromDirectory` — four independent, structurally near-identical directory-scan-and-validate loops (glob a directory, ordinal-sort filenames, try/catch-deserialize per file, report a skip via an `Action<string,...>` callback, collect the survivors).
reason: summary: this story's `LoadSelectableFromDirectory` deliberately mirrors `AbilityRegistry.LoadFromDirectory`'s established pattern (named explicitly in this story's own spec Code Map) rather than inventing a new one — the right per-story call given three prior loaders already established the convention. But the convention itself has now been copy-pasted a fourth time with no shared extraction, the same "hand-duplicated in a THIRD independent place" class of issue this ledger already flags elsewhere (DW-98, for a different concern: the combat/economy category list). Surfaced by the Blind Hunter review layer during this story's own review pass; not a defect in THIS story's code (which correctly follows established precedent) so much as an observation that the precedent itself is due for a shared-helper extraction.
closure: low priority, purely a DRY/maintainability concern with no behavioral impact — if a 5th such loader is ever added, extract a shared `LoadValidatedFromDirectory<T>(absDir, string pattern, Func<string, (bool ok, T value, string error)> tryParse, Action<string,string>? onExcluded)` helper (or similar) that all four (eventually five+) call sites delegate to, rather than continuing to hand-duplicate the loop.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Pure DRY/maintainability observation with no behavioral impact; the entry's own closure defers extracting the shared loader until a 5th such loader is added — that trigger has not occurred.

### DW-123: `ScenarioLoadPhase.ResolveSlotFactionDefs`'s `FactionDefinition.LoadFromFile(abs)` call still THROWS (uncaught) if a scenario-assigned faction file fails the lenient `Validate` check — pre-existing, not introduced by Story 5.7
source_spec: `_bmad-output/implementation-artifacts/spec-5-7-wizard-authored-factions-are-immediately-selectable-in-playtest-skirmish-fr-19-ux-dr80.md`
location: `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` (`ResolveSlotFactionDefs`'s `var def = FactionDefinition.LoadFromFile(abs);` call, unguarded by any try/catch); `godot/src/Core/Definitions/FactionDefinition.cs` (`LoadFromFile` throws `InvalidOperationException` on a `Validate` failure — pre-existing since Story 5.2, unchanged by this story).
reason: summary: if a scenario's `PlayerSlots[].FactionJson` names a file that was valid when the scenario was authored but is later hand-edited (or corrupted) to fail the lenient `FactionValidator.Validate` check (e.g. a genuinely malformed color array or duplicate unit id — NOT the roster-completeness axis this story's new `ValidateComplete` diagnostic covers), `LoadFromFile` throws uncaught, propagating out of `ResolveSlotFactionDefs` and aborting the entire scenario load / `MainScene._Ready` — a hard crash instead of a graceful "this slot's faction is broken" outcome. This is NOT new: `LoadFromFile`'s throwing behavior and this call site's lack of a guard both predate Story 5.7 (Story 5.2 authored `LoadFromFile`; `ResolveSlotFactionDefs`'s call to it predates this story's diff, which only added code AFTER this line). Surfaced incidentally by the Edge Case Hunter review layer while reviewing this story's diff, since the new `ValidateComplete` diagnostic sits immediately after this unguarded call.
closure: wrap `ResolveSlotFactionDefs`'s `FactionDefinition.LoadFromFile(abs)` call in a try/catch (mirroring the graceful `ScenarioSerializer.LoadFromFile`-returns-null-on-parse-failure fallback pattern `LoadAndApplyScenario` already uses one level up), logging the located error and leaving that slot's `SlotFactionDefs` entry at its prior default rather than crashing the whole scene load. Low priority — requires a scenario file to be hand-edited to a genuinely malformed (not just incomplete) faction reference, which no current authoring surface produces.
status: open
decision: 2026-07-28 correct-course — bundle faction-load-error-handling extended to faction-load-fail-closed (+DW-317 card-panel call sites; Epic 15, Story 15.6)

### DW-124: `ai_preset` is validated data but not consumed by any runtime AI system — `AiOpponentSystem` always pilots via `AiDifficulty`, never `ai_preset`

source_spec: `_bmad-output/implementation-artifacts/spec-5-8-playtest-validate-asymmetry-ai-playability-of-the-showcase-factions-fr-20-fr-18.md`
location: `godot/src/Core/Definitions/FactionDefinition.cs` (`AiPreset`, a validated closed set of exactly one value, `"balanced"` — see `FactionValidator`); `godot/src/AI/AiOpponentSystem.cs` (the sole runtime AI, whose constructor takes only an `AiDifficulty` and never reads `FactionDefinition.AiPreset` anywhere in its scoring/build/train/attack logic); `godot/src/Core/Sim/SimulationHost.cs` (`Create`'s `aiLevel: AiDifficulty` parameter — the only behavior knob ever threaded to the AI).
reason: summary: this story piloted both showcase factions (alpha/beta) through the existing single `AiOpponentSystem` by swapping which `FactionDefinition` fills `SimulationHost.Create`'s `factionDef2` slot, proving FR-18's "AI-playable" bar for each real roster. That is a legitimate, zero-new-production-code way to exercise "each side run by the AI" — but it does NOT make "each side run by its `ai_preset`" literally true: `AiOpponentSystem` never reads `ai_preset` at all, and today the field is a closed set of exactly one value (`"balanced"`), so there is no per-preset behavioral differentiation to even observe yet. Both factions' matches in this story ran under the identical `AiDifficulty.Normal` weights regardless of their (currently-identical) `ai_preset` values. Confirmed by grep: `ai_preset`/`AiPreset` appears only in `FactionDefinition`, `FactionValidator`, the Faction Definer wizard UI, and faction JSON — never in `AiOpponentSystem`, `SimulationHost`, or any other runtime system.
closure: a future story must either (a) wire `AiOpponentSystem` to read `FactionDefinition.AiPreset` and vary its scoring weights/thresholds per preset (mirroring how `AiDifficulty` already does this), expanding the closed set beyond `"balanced"` first if distinct presets are meant to exist, or (b) explicitly retire the "each side run by its ai_preset" framing in favor of the difficulty-only reality if per-preset AI behavior is never built. Out of this story's scope — it is integration/validation-only over the existing 5.1-5.7 systems, and adding preset-driven AI behavior would be new production capability.
status: open
decision: 2026-07-25 Keep open until a faction-AI story — Defer the choice to whenever distinct AI presets are actually designed
decision: 2026-07-25 Keep open until a faction-AI story — Defer the choice to whenever distinct AI presets are actually designed
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 10.10 (faction-AI presets)

### DW-125: `AsymmetryPlaytestValidationTests`' harness constants (`InitialWaveSize = 5`, the AI/opposing base positions `(45,0,0)`/`(-45,0,0)`) hand-copy `AiOpponentSystem`'s internal attack-threshold and `P1_BASE` values rather than referencing them symbolically
source_spec: `_bmad-output/implementation-artifacts/spec-5-8-playtest-validate-asymmetry-ai-playability-of-the-showcase-factions-fr-20-fr-18.md`
location: `godot/ProjectChimera.Sim.Tests/Golden/AsymmetryPlaytestValidationTests.cs` (`InitialWaveSize`, the AI/opposing `FixedVec3` base positions in `BuildAiPilotedHarness`); `godot/src/AI/AiOpponentSystem.cs` (the Normal-difficulty attack-unit threshold and the hardcoded `P1_BASE` attack destination these constants mirror).
reason: summary: the test's initial-wave size and base positions were deliberately chosen to match `AiOpponentSystem`'s current internal attack threshold and hardcoded attack destination, but `AiOpponentSystem` exposes neither as a public/internal symbol the test can reference — the test file hand-copies the values as its own local constants instead. If a future story retunes those internal values (e.g. a difficulty-curve pass), this test keeps compiling and may keep passing in a degraded way (wave no longer meets the real attack threshold, or the AI's attack destination no longer lines up with where defenders were placed) rather than failing loudly or failing to compile — a silent-staleness risk, not a correctness bug today. Flagged by the Blind Hunter review layer; independently anticipated by the implementing agent's own residual-risks note.
closure: low priority — when `AiOpponentSystem`'s attack-threshold/`P1_BASE` constants are next touched (e.g. an AI difficulty/balance story), either expose them as `internal` with `InternalsVisibleTo(ProjectChimera.Sim.Tests)` so this test can reference them symbolically instead of duplicating literals, or add a comment cross-reference + a cheap assertion in this test file that fails loudly if the mirrored values drift from the real constants.
status: open

## Deferred from: spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable (2026-07-11)

### DW-126: No standalone "Scenario Settings" panel — win condition is only creator-editable via `OnboardingPanel`'s embedded picker (or the pre-existing Edit-mode `WinConditionUi` corner panel)
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/UI/OnboardingPanel.cs` (`BuildWinConditionStep`/`AddWinConditionButton` — the only NEW win-condition control this story adds); `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` (the pre-existing always-on Edit-mode corner panel with the same Destroy-All-Buildings/Eliminate-All-Units toggle, which this story's onboarding control duplicates rather than replaces).
reason: summary: this story's spec explicitly scoped the win-condition picker to live "only inside OnboardingPanel for this story," deliberately deferring a general-purpose Scenario Settings surface. Two controls now exist that both mutate the same `ScenarioData.WinCondition` (the pre-existing `WinConditionPhase` corner panel, always visible in Edit mode, and this story's onboarding-embedded picker, visible only while the walkthrough is open). A review pass found the corner panel's radio *display* went stale after an external write (it only snapshotted `ButtonPressed` once, at construction) — patched via `SceneContext.WinConditionUiRefresh`/`MainScene.RefreshWinConditionUi`, so both surfaces now stay visually in sync — but the underlying duplication (two separate UI surfaces for one field) remains a real structural gap a future Scenario Settings panel should absorb into one place instead of two.
closure: when a general-purpose Scenario Settings panel is eventually built (map name/author, win condition, player-slot starting resources, etc.), fold the win-condition control into it and either remove `OnboardingPanel`'s copy (pointing that step at the new panel instead) or leave both as a deliberate redundant on-ramp — not urgent, no functional conflict today.
status: open
decision: 2026-07-25 Build a unified Scenario Settings panel — Create a Scenario Settings surface (map name/author, win condition, per-slot starting resources) and fold the win-condition control into it, removing/redirecting the duplicate.

### DW-127: No "New Scenario" empty-canvas origination flow — onboarding operates on whichever scenario is already loaded at boot (fallback/default/JSON)
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` (`LoadAndApplyScenario` — scenarios only ever originate from an AI-generated hand-off, a JSON file, or the hardcoded fallback; no `new ScenarioData()`-from-empty UI path exists anywhere in `src/`); `godot/src/UI/OnboardingPanel.cs` (`BuildPlaceEntitiesStep` — step 4 instructs the creator to use `EntityPlacer`'s existing palette on the CURRENT scenario rather than an empty canvas).
reason: summary: confirmed by this story's own Design Notes (no `new ScenarioData()` call, no Create/New-Scenario UI anywhere in the codebase) — building a true empty-canvas origination flow is Epic-6-scale scope this story doesn't own. Onboarding instead coaches the creator to place a base + units onto whichever scenario booted (fallback/default/JSON), which still satisfies the story's testable "produce a playable scenario in under 15 minutes" bar without inventing scope the epic didn't ask for. Not a defect — a deliberate, spec-documented scope boundary.
closure: when a future epic (Epic 6-scale) adds a real "New Scenario" empty-canvas flow, onboarding step 4 (and its instructional copy) should be revisited to offer starting from a blank canvas as an alternative to editing the boot-time scenario. Not urgent — no creator-facing complaint, and the current flow is fully functional for its stated 15-minute-playable goal.
status: open
decision: 2026-07-25 Build a New Scenario empty-canvas flow — Add a Create/New-Scenario UI that originates a blank ScenarioData, and revisit onboarding step 4 to offer starting from a blank canvas.

### DW-128: Onboarding step 1's unit "template" list is a small curated fixed array (`UnitCardPanel.CuratedTemplateUnits`), not a browsable gallery
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/CreationSuite/UnitCardPanel.cs` (`CuratedTemplateUnits` — hardcoded to `worker`/`infantry`/`archer`).
reason: summary: the spec's own Boundaries/Never section explicitly scopes this to "a small fixed list of 2-3 existing unit ids, opened via one OnboardingPanel button through the existing Duplicate path" and explicitly excludes a curated-template gallery UI — so the fixed list itself is intentional, not a gap. UPDATE (review pass 1): the original silent-fallback concern (a missing curated id opening the editor with no explanation) is now PATCHED — `StartFromTemplate`/`MainScene.OpenUnitCardPanel` return whether the duplicate happened, and `OnboardingPanel` shows a distinct warning-colored note on the fallback path instead of staying silent. What remains open is narrower: today both shipped factions (alpha/beta) carry all three curated ids, so the fallback path is unreachable in practice.
closure: low priority given today's unreachability — if/when a non-alpha/beta faction becomes a plausible boot default, consider widening `CuratedTemplateUnits` to something resolved dynamically (e.g. "first Worker-category unit found" + "first combat unit found") instead of a hardcoded id list.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — The fixed 2-3 curated-id list is an explicit spec Boundaries/Never scope decision (no gallery UI), and the original silent-fallback concern is already patched with a distinct warning note; the only residual reachability is tracked separately by DW-135. Nothing to build here on its own.

### DW-129: `OnboardingPanel`'s `CanvasLayer` (Layer = 14) collides with `AbilityEditorPanel`'s (also Layer = 14)
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/UI/OnboardingPanel.cs` (`BuildUi`, `_canvas = new CanvasLayer { Layer = 14 }`); `godot/src/CreationSuite/AbilityEditorPanel.cs:129` (same value).
reason: summary: undefined stacking order if both panels are visible simultaneously (flagged by the Blind Hunter review layer). The same pattern already exists elsewhere in the codebase without documented issue — `BuildingCardPanel`, `FactionDefinerPanel`, `MapGeneratorPanel`, and `ResearchCardPanel` all already share Layer = 13 — but those four are opened via mutually-exclusive hotkey toggles, whereas `OnboardingPanel` is deliberately non-modal and stays open WHILE a creator drives other panels, making simultaneous visibility with `AbilityEditorPanel` more plausible than the existing precedent. Real but low consequence: worst case is a cosmetic z-order glitch (both panels remain fully interactive; neither blocks input).
closure: not urgent — if/when the project does a full CanvasLayer registry pass (or the next time a layer collision causes an actual visible glitch), assign `OnboardingPanel` a layer that doesn't collide with any single-open-at-a-time-optional panel it can coexist with.
status: open

### DW-130: `OnboardingPanel` has no Escape-key dismissal, unlike every sibling Creation Suite panel
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/UI/OnboardingPanel.cs` (no `_Input`/`_UnhandledInput` override); contrast `godot/src/CreationSuite/BuildingCardPanel.cs`, `TriggerEditorPanel.cs`, `UnitCardPanel.cs`, `TerrainBrush.cs`, `godot/src/UI/SettingsPanel.cs` (all wire Escape to close).
reason: summary: flagged by the Blind Hunter review layer — the panel can currently only be closed via Skip, Finish, or reaching Play (mouse-only). Not patched in this pass because `MainScene`'s existing global Escape handler already toggles the Settings panel unconditionally on every Escape press (`MainScene.cs` `_UnhandledInput`), and adding a second Escape consumer risks a genuinely new conflicting-Escape bug (both firing on the same press, or an ambiguous precedence) rather than a safe mechanical addition — a deliberate precedence decision, not a blind patch.
closure: when Escape-key handling across Creation Suite panels is next touched, decide the precedence explicitly (e.g. onboarding consumes Escape only when it's the topmost visible overlay and Settings is closed) and wire `OnboardingPanel` to match its siblings.
status: open
decision: 2026-07-19 Close on Escape only when onboarding is topmost and Settings is closed — Add _UnhandledInput closing the panel on Escape only when it is the topmost visible overlay and Settings is not open, calling SetInputAsHandled to block the global Settings toggle.

### DW-131: Onboarding steps 2/3 don't verify the creator is still editing the SAME unit `StartFromTemplate` created in step 1
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/UI/OnboardingPanel.cs` (`BuildTuneStatsStep`, `BuildPromoteHeroStep` — both just call `_mainScene?.OpenUnitCardPanel()` with no template id, trusting whatever unit `UnitCardPanel` currently has bound).
reason: summary: flagged by the Blind Hunter review layer — because onboarding is deliberately non-modal and never blocks normal panel interaction, a creator who duplicates/deletes/navigates to a different unit inside the Unit Editor between steps 1 and 2 will have step 2/3's instructions ("tune its stats," "promote it to Hero") silently apply to whichever unit happens to be bound, not necessarily the one just created. Low severity (self-correcting — the creator can see which unit is open) but a real first-time-creator confusion vector, and not a small self-contained fix (requires threading an "onboarding's unit id" concept through `UnitCardPanel`'s bind state).
closure: when onboarding is next revisited, consider having `OnboardingPanel` remember the created unit's id (from step 1) and either re-bind to it explicitly on steps 2/3, or surface a note when the currently-open unit doesn't match.
status: open

### DW-132: `OnboardingPanel`'s own step-navigation/win-condition-mutation/Skip-Finish-"seen" logic has zero direct test coverage
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/UI/OnboardingPanel.cs` (431 lines, no dedicated test file); only `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` (bootstrap ordering) and `godot/ProjectChimera.Sim.Tests/Definitions/SettingsDataRoundTripTests.cs` (DTO serialization) touch anything related.
reason: summary: flagged independently by the Blind Hunter review layer and the Intent Alignment Auditor. This is NOT a deviation this story introduced — no Creation Suite panel in the codebase has direct GdUnit4/Control-level test coverage (Godot `Control`-based UI logic is verified via live `/godot-verify` sessions project-wide, not automated tests) — but `OnboardingPanel` is unusually high-visibility (a first-time creator's first impression) and unusually large (431 lines) for a story with only a live-verification safety net.
closure: if this project ever invests in GdUnit4 Control-level test infrastructure, `OnboardingPanel`'s step-navigation and win-condition-mutation logic is a strong first candidate; until then, treat any future change to this file as needing a live `/godot-verify` pass, not a `dotnet test` pass, to catch regressions.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Project-wide convention verifies Godot Control-based UI via live /godot-verify sessions, not automated GdUnit4/Control-level tests; a deliberate testing-approach boundary the project excludes, and the infrastructure to write such a test does not exist. Any future OnboardingPanel change goes through a live godot-verify pass.

## Deferred from: spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable (2026-07-11 review pass 2)

### DW-133: Simple-mode and Advanced-mode Unit-Card Ultimate pickers can display divergent selections for the same field
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/CreationSuite/UnitCardPanel.Edit.cs` (the new Simple-mode Ultimate row at ~line 141 and the pre-existing `BuildHeroAdvanced` Ultimate row, both `AddHeroAbilityRow` bindings against `def.Hero.UltimateAbility`; `OnSegmentChanged` at ~line 207 toggles only `_advancedHost.Visible` and never rebuilds).
reason: summary: this story added a second `OptionButton` bound to `HeroDefinition.UltimateAbility` (the Simple-mode Ultimate picker) while the Advanced-mode one already existed; both are built simultaneously when the unit is a hero. Because the segment toggle only flips visibility (no rebuild), changing the value in one dropdown does not update the other's displayed `Selected` index until the next full form rebuild (Bind / navigate / promote-toggle). Flagged independently by the Blind Hunter and Edge Case Hunter layers. LOW consequence: the underlying `def.Hero.UltimateAbility` field — the single source of truth used by Save and the validator — is always correct; only the *displayed* selection of the currently-hidden pane can be stale, and re-selecting in that pane (or any rebuild) reconciles it. Not patched this pass because the clean fix (rebuild-on-segment-toggle, or a live two-way sync between the two OptionButtons) is a deliberate change to the panel's established read-on-build / write-on-change design with scroll/focus implications, not a safe mechanical edit.
closure: when the Unit Card panel's Simple/Advanced sync strategy is next revisited, either rebuild the body on segment change (accepting the scroll/focus reset the rest of the panel already incurs on Bind) or have both Ultimate `OptionButton`s re-read `def.Hero.UltimateAbility` when their pane becomes visible. Low priority — no data corruption, self-reconciling.
status: open

### DW-134: `SettingsDataRoundTripTests` hand-replicates `SettingsManager`'s `JsonSerializerOptions` instead of exercising the real serializer path, so silent divergence is unguarded
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/ProjectChimera.Sim.Tests/Definitions/SettingsDataRoundTripTests.cs` (local `Opts` `JsonSerializerOptions`); `godot/src/UI/SettingsManager.cs` (`_jsonOpts`, the real Load/Save options — a Godot `Node`, hence unloadable in the Godot-free `ProjectChimera.Sim.Tests` assembly).
reason: summary: the round-trip test hand-rolls a `JsonSerializerOptions` that is currently byte-for-byte identical to `SettingsManager._jsonOpts` (verified: `WriteIndented` / `ReadCommentHandling.Skip` / `AllowTrailingCommas`), but nothing enforces that they stay in sync. If `SettingsManager`'s real options later gain a naming policy or converter, `HasSeenOnboarding` (or any field's) persistence could regress while this "round-trip" suite stays green, because the suite validates the DTO in isolation, not the real serializer. Flagged by the Blind Hunter and Verification Gap layers. This matches the spec's own Verification plan (which specified a DTO round-trip) and is bounded by an architectural constraint — `SettingsManager` is a Godot `Node` that cannot be constructed in the headless sim test assembly — so it is a real latent gap rather than a deviation this story introduced. LOW consequence today (options are identical).
closure: extract the shared `JsonSerializerOptions` into a Godot-free static (e.g. a `SettingsSerialization.Options` in `src/Core/Definitions`) that both `SettingsManager` and the test reference, so divergence becomes impossible; or add a guard test that reflects over `SettingsManager`'s options once the type is reachable. Low priority.
status: open

### DW-135: Onboarding step 1's curated template ids dead-end when the boot-time faction lacks worker/infantry/archer
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/CreationSuite/UnitCardPanel.cs` (`CuratedTemplateUnits` = `worker`/`infantry`/`archer`; `StartFromTemplate`'s not-found path); `godot/src/UI/OnboardingPanel.cs` (step-1 template buttons).
reason: summary: distinct from DW-128 (which tracks the *fixed-list vs gallery* scope decision and the now-patched silent-fallback feedback). This entry tracks the residual behavioral edge: if a creator boots with an active faction that contains none of the three curated ids (e.g. a user-authored faction), all three step-1 template buttons hit the graceful "Couldn't find template…" warning and step 1 creates nothing — a dead end for that creator. Flagged by the Blind Hunter and Edge Case Hunter layers. LOW consequence today: both shipped alpha/beta factions carry all three ids, so the boot-time scenario the onboarding operates on has working templates; the dead-end is only reachable once a custom faction can be the boot default.
closure: when a non-alpha/beta faction can plausibly be the boot default, resolve the curated list dynamically (e.g. "first Worker-category unit" + "first combat unit" from the active faction) instead of hardcoded ids. Overlaps DW-128's closure note. Low priority.
status: open

### DW-136: `OnboardingPanel`'s bottom-anchored position uses a fixed `PANEL_H` while the panel's real height is content-driven
source_spec: `_bmad-output/implementation-artifacts/spec-5-9-added-your-first-scenario-guided-onboarding-15-min-playable.md`
location: `godot/src/UI/OnboardingPanel.cs` (`PANEL_H = 360` constant vs the `CustomMinimumSize` height of 0 and the `BottomLeft`-anchored `Position.Y = -(PANEL_H + MARGIN)`).
reason: summary: the overlay is bottom-left anchored at a fixed vertical offset computed from `PANEL_H = 360`, but its actual height is driven by content (step body length + wrapped note label + footer). On a step whose content exceeds ~360px the panel can extend below the intended bottom (footer Back/Next potentially off the viewport edge); on shorter steps it leaves a gap. Flagged by the Blind Hunter layer. Not verified to actually clip on any current step (step bodies are short), so tracked as a robustness/latent-layout item rather than a confirmed clip. LOW consequence.
closure: when onboarding layout is next touched, either size the anchor offset from the panel's measured height (`Size.Y` after `ResetSize`) or cap the body in a `ScrollContainer` so the footer is always reachable regardless of content length. Low priority.
status: done 2026-07-19
resolution: already resolved: OnboardingPanel.cs:211-223 now wraps the per-step body in a ScrollContainer (CustomMinimumSize 0,150; HorizontalScrollMode Disabled) with the Back/Next footer added AFTER it in the root VBox (:231-253) — exactly the recommended fix, so long content scrolls and the footer never clips.

### DW-137: Editor-placed map items (Story 3.15) are not synced into `ScenarioData` and are lost on save/reload and F5
source_spec: `_bmad-output/implementation-artifacts/spec-6-1-verify-harden-the-creation-suite-editor-terrain-sculpt-paint-entity-start-resource-win-placement-to-ship-bar.md`
location: `godot/src/UI/EntityPlacer.cs` (`PlacementMode.Item`, Story 3.15) + `godot/src/Core/MainScene.cs` (Story 6.1 sync handlers) — no `_onItemSync` callback exists; `ScenarioData.Items` / `ScenarioItem` are never written by the editor.
reason: summary: Story 6.1 added `ScenarioData` sync for buildings/units/resource-nodes (its explicitly enumerated scope) but the editor's Item placement mode has the same latent defect the story fixed for the other kinds — a placed item is never mirrored into `_ctx.Scenario.Items`, so it is silently lost on save/reload and on the F5 Edit→Play re-apply (`ResetToAuthoredStart` re-applies only `_ctx.Scenario`). Surfaced by the Blind Hunter review layer. Out of scope for 6.1 (the intent enumerates only buildings/units/resource-nodes), so deferred rather than folded in. MEDIUM consequence (placed items vanish with no signal).
closure: mirror the buildings/units/resource-nodes sync for items — add an `_onItemSync` callback (same `ScenarioSyncOp` opaque-handle protocol) fired inside `PlaceItem`/`DeleteItem`'s `_history.Push` closures, and a `MainScene.SyncItem` handler mutating `_ctx.Scenario.Items`. Consider whether item placement should reuse the identity-preserving delete/undo pattern from 6.1.
status: open

### DW-138: `EntityPlacer._history` is not cleared across the F5 Edit→Play→Edit round-trip — a post-F5 undo can now corrupt `ScenarioData`
source_spec: `_bmad-output/implementation-artifacts/spec-6-1-verify-harden-the-creation-suite-editor-terrain-sculpt-paint-entity-start-resource-win-placement-to-ship-bar.md`
location: `godot/src/UI/EntityPlacer.cs` (`_history` / `EditorHistory`) + `godot/src/Core/MainScene.cs` (`ResetToAuthoredStart` / `ResetMatchOnReturnToEdit` — neither clears `_history`).
reason: summary: The undo/redo history survives an Edit→Play→Edit toggle (only the internal `_redoStack` is ever cleared). This was already a latent hazard for the live stores (post-F5 undo closures reference stale `capturedId` slot ids after `ClearForReset` + re-apply). Story 6.1's sync now routes those same closures into `_ctx.Scenario` too, so a post-F5 Ctrl+Z/Y can strip or re-add a scenario entry that no longer corresponds to the current live entity — corrupting the persisted board. Pre-existing root cause (stale-history-after-reset), newly reaching `ScenarioData` via this diff. Surfaced by the Verification Gap review layer. MEDIUM consequence, narrow trigger (requires an F5 toggle followed by an undo).
closure: clear (or otherwise invalidate) `EntityPlacer._history` inside `ResetToAuthoredStart` / on return to Edit so no undo/redo closure can outlive the reset that made its captured ids stale. Fixes both the pre-existing live-store hazard and the new `ScenarioData` exposure in one place.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-editor-history-core

### DW-139: Follow-up review still recommended for 6-6-doodads-props-placement-editor-multi-select-copy-paste-rotation-named-cameras-water-floor after the review budget was exhausted
origin: review-budget-followup
source_spec: `spec-6-6-doodads-props-placement-editor-multi-select-copy-paste-rotation-named-cameras-water-floor.md`
severity: low
reason: Review budget (2 cycles) was exhausted with the story finalized (status: done, verify green) while the review pass kept recommending an independent follow-up. The work was committed by bmad-loop run 20260714-104223-7fe9; this entry preserves the lingering follow-up recommendation for a deliberate later review.
status: done 2026-07-16
resolution: closed by human decision: Story shipped and verified green; accept the residual recommendation as satisfied.
decision: 2026-07-16 Close as reviewed — Story shipped and verified green; accept the residual recommendation as satisfied.

### DW-140: Terrain stroke undo pins unbounded Image memory on the shared EditorHistory

origin: code review of spec-6-2-persist-sculpted-terrain-height-painted-textures-across-save-load-stroke-undo-redo-headline-defect-fix.md, 2026-07-14 (epic-6 bmad-loop; normalized from a flat append at the 2026-07-15 retro sweep)
location: godot/src/CreationSuite/TerrainBrush.cs (SnapshotRegions/PushStrokeUndo) + godot/src/CreationSuite/EditorHistory.cs
severity: medium
reason: Each stroke deep-`Duplicate`s height+control Images (before AND after) per touched region onto an uncapped shared EditorHistory — a long sculpt session pins hundreds of MB–GB of undo memory. A cap/coalescing policy also affects the shared entity-undo semantics, so it needs a deliberate design, not a drive-by patch.
status: done 2026-07-27
resolution: resolved by sweep bundle dw-editor-history-core
decision: 2026-07-25 Byte-capped coalescing shared policy — Add a byte/size cap to EditorHistory dropping oldest entries beyond the cap, shared across terrain + entity undo
decision: 2026-07-25 Byte-capped coalescing shared policy — Add a byte/size cap to EditorHistory dropping oldest entries beyond the cap, shared across terrain + entity undo
decision: 2026-07-15 Bounded/coalescing history policy — Introduce a size/byte-capped, coalescing EditorHistory policy shared across terrain-stroke and entity undo, dropping oldest entries beyond the cap.
decision: 2026-07-15 Bounded/coalescing history policy — Introduce a size/byte-capped, coalescing EditorHistory policy shared across terrain-stroke and entity undo, dropping oldest entries beyond the cap.

### DW-141: A stroke that auto-creates a new Terrain3D region cannot be undone

origin: code review of spec-6-2 (terrain persistence), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/CreationSuite/TerrainBrush.cs (SnapshotRegions skips null get_region probes; RestoreRegions re-imports only snapshotted regions)
severity: low
reason: Painting into empty space makes Terrain3D auto-create a region with no pre-stroke snapshot, so undo leaves the new region in place. Fix needs was-absent tracking + remove_region on undo. Narrow (map-expansion strokes) but a real undo-correctness gap.
status: done 2026-07-28
resolution: resolved by sweep bundle dw-terrain-brush-stroke-lifecycle

### DW-142: Pressing T mid-drag strands _isPainting — EndPaint never runs, leaking the pending stroke snapshot and its undo entry

origin: code review of spec-6-2 (terrain persistence), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/CreationSuite/TerrainBrush.cs (_Input early-returns on !_brushActive ~:189)
severity: low
reason: Pre-existing phantom-motion-paint input-state-machine root; 6.2's new aspect is the retained _strokeBefore Image snapshot and the lost undo entry. Mouse-release during a T-deactivated drag never reaches EndPaint, and the motion+_isPainting branch then paints buttonlessly on re-activation.
status: done 2026-07-28
resolution: resolved by sweep bundle dw-terrain-brush-stroke-lifecycle

### DW-143: No-op terrain strokes push undo commands onto the shared EditorHistory — a later Ctrl+Z is silently absorbed

origin: code review of spec-6-2 (terrain persistence, review pass 3 Blind Hunter), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/CreationSuite/TerrainBrush.cs (EndPaint→PushStrokeUndo pushes whenever ≥1 region snapshotted, no before-vs-after equality check)
severity: low
reason: A stroke that changed nothing (e.g. paint-mode strokes while TOOL_TEXTURE write is broken [see story 10-16], or flatten on already-flat terrain) still pushes an undo command, so interleaved undo requires an extra Ctrl+Z that visibly does nothing. Clean fix needs cheap stroke-changed-anything detection on the hot path. No data loss.
status: done 2026-07-28
resolution: resolved by sweep bundle dw-terrain-brush-stroke-lifecycle

### DW-144: Ctrl+Z is not blocked while a terrain stroke is in progress — undo races the live operate() and corrupts the undo entry

origin: code review of spec-6-2 (terrain persistence, review pass 3 Blind Hunter), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/UI/EntityPlacer.cs (_UnhandledInput Ctrl+Z handling) + godot/src/CreationSuite/TerrainBrush.cs (_isPainting)
severity: low
reason: Ctrl+Z with LMB held races RestoreRegions (writing the previous stroke's images) against the live operate() sculpting the same region; EndPaint then captures `after` from the mixed state. Narrow trigger, genuine concurrency hole; a clean fix is cross-node (EntityPlacer consults TerrainBrush._isPainting, or the brush swallows undo mid-stroke).
status: done 2026-07-28
resolution: resolved by sweep bundle dw-terrain-brush-stroke-lifecycle

### DW-145: Import leaves a stale author-local TerrainRef when a package bundles zero terrain files

origin: code review of spec-6-2 (terrain persistence, review pass 3 — converged Blind/Edge/Verification-Gap), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/CreationSuite/WinConditionPhase.cs (DoImport — `if (result.TerrainFiles.Count > 0)` has no else clearing TerrainRef)
severity: low
reason: Only reachable via hand-built/third-party/cross-version packages (this system exports TerrainRef non-empty IFF terrain files are bundled), so tracked rather than patched into the settled change. Consequence: graceful flat fallback + a repeated "TerrainRef folder missing" PrintErr on every load instead of log-once. Fix: `else { imported.TerrainRef=""; SaveToFile(...); }` in the import path.
status: open
decision: 2026-07-28 correct-course — bundle content-package-import-roundtrip merged into map-package-import-one-path (DW-235; Epic 15, Story 15.6)

### DW-146: SimChecksum-folded elevation grid is built via Godot float get_height → Fixed.FromFloat — cross-platform determinism risk

origin: code review of spec-6-3-sim-side-deterministic-terrain-elevation-height-advantage-vision-and-fog-of-war-verify.md (Blind Hunter Finding 3), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/Core/ScenarioLoadPhase.cs (BuildAndInjectElevationGrid)
severity: medium
reason: `Terrain3DData.get_height` does float bilinear interpolation whose last bit can differ across ARM/x64 (FMA/rounding); `Fixed.FromFloat`'s `(int)(v*65536)` truncation can then cross a boundary → divergent `Elevation.Raw` → SimChecksum desync between heterogeneous peers. NOT reachable today: all shipped/golden scenarios are flat (Elevation==0) and every TICKING client is x64 (the server is a relay+quorum collector and does not tick — the companion "headless server desync" finding was REJECTED on that evidence). Becomes real when a sculpted map ships on cross-platform clients. Proper fix reads RAW per-region height-map cells per the epic's "never Godot Image interpolation" rule. Flagged as the epic-6 retro's standing determinism watch-item.
status: open
decision: 2026-07-25 Read raw heightmap cells now — Rewrite BuildAndInjectElevationGrid to read raw per-region heightmap cells (no Godot Image interpolation), pre-empting the divergence.
decision: 2026-07-19 Defer until cross-platform
decision: 2026-07-28 correct-course — filed as Story 15.2 (Epic 15, map-size determinism unification)

### DW-147: MovementSystem blocked-cell rejection tests only endpoint cells — a fast unit can tunnel a 1-cell wall in one tick

origin: code review of spec-6-5-impassable-terrain-pathability-paint-deterministic-blocking-and-the-pathability-overlay.md (Blind #1, Edge E2), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/Core/MovementSystem.cs (rejection checks IsBlocked(pos)/IsBlocked(np) only; CELL_SIZE_WORLD=2, dt=1/30)
severity: low
reason: A per-tick displacement ≥ the 2-unit cell size (move speed ≥ ~60 world-units/s) or a 1-cell-thick wall can be tunnelled (both endpoints clear). Unreachable with shipping content (golden mover = 1.33 u/tick; authored walls thicker than one cell). Fix: swept-cell (DDA) check pos→np rejecting on the first blocked cell crossed.
status: open

### DW-148: Slope-DERIVED blocked cells escape ScenarioValidator, and an already-in-blocked unit traverses blocked cells freely

origin: code review of spec-6-5 (pathability; Blind #4, Edge E3/E4, Intent-Alignment d), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/Core/Definitions/ScenarioValidator.cs (CheckNotBlocked decodes only the painted layer) + godot/src/Core/MovementSystem.cs (rejects only a CROSSING: !IsBlocked(pos) && IsBlocked(np))
severity: medium
reason: The heightmap isn't available at the Godot-free pre-tick gate, so a spawn on a slope-derived blocked cell ships un-caught, and once inside any blocked cell a unit is exempt from further blocking. Narrow (slope-auto-block is per-map default OFF; the overlay shows steep cells). Fix: a load-time spawn-in-blocked guard against the resolved union grid (after BuildAndInjectPathabilityGrid), and/or confine an in-blocked unit to moves toward a clear cell.
status: open

### DW-149: Slope derivation samples only +X/+Z — far east/south edge cells never auto-block and cliff walls land one cell to the low side

origin: code review of spec-6-5 (pathability; Blind #7, Edge E5), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/Core/PathabilityGrid.cs (DeriveSlopeBlockedInto — forward-neighbour differences only; ElevationGrid.Sample clamps at max col/row ⇒ rise 0)
severity: low
reason: Deterministic (not a determinism bug) and the feature is per-map default OFF; the asymmetry is a quality gap. Fix: max-over-4-neighbours or central difference (skipping clamp-equal neighbours).
status: open

### DW-150: PathabilityTool re-implements FlowField.WorldToCell locally — nothing pins editor-painted cell == sim-blocked cell

origin: code review of spec-6-5 (pathability; VG5), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/CreationSuite/PathabilityTool.cs (private WorldToCell/CellCenter vs FlowField's)
severity: low
reason: They agree today (Mathf.FloorToInt == Fixed.ToInt() arithmetic shift, verified), but a future change to the sim mapping would silently desync what the editor paints from what the sim blocks. Fix: route the tool's cell mapping through the shared FlowField methods or a shared Godot-free helper.
status: open

### DW-151: Group move/duplicate/paste re-derives placements lossily (worker overrides, pre_built, node collection/owner fields)

origin: code review of spec-6-6-doodads-props-placement-editor-multi-select-copy-paste-rotation-named-cameras-water-floor.md, 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/UI/EntityPlacer.cs (Describe captures only id/faction/position [+supply/rate for nodes]; BuildCreate respawns from scratch)
severity: medium
reason: A moved/duplicated worker respawns via the combat path losing worker overrides; a building loses its authored pre_built flag; a node loses its Story-4.7 collection/owner fields. Single-entity delete/undo paths ARE identity-preserving — only the multi-select move/copy/paste path is lossy. Authoring-fidelity only, no determinism/checksum impact.
status: open

### DW-152: Rotation persists on units/buildings/nodes but only props apply visual yaw at spawn

origin: code review of spec-6-6 (doodads/multi-select), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/UI/BuildingBridge.cs / MultiMeshBridge (no per-entity rotation channel); PropRenderer is the sole .Rot consumer
severity: low
reason: `Rot` round-trips (hash-excluded) on all placeables, but the sim-render bridges have no per-entity rotation channel, so non-prop yaw is cosmetic-only-unrendered. Footprints stay axis-aligned for 1.0 by design; wiring non-prop yaw is architecturally invasive for low value.
status: done 2026-07-19
resolution: closed by human decision: Accept yaw as prop-only / cosmetic-unrendered for non-props; footprints are axis-aligned by design.
decision: 2026-07-19 Keep prop-only — Accept yaw as prop-only / cosmetic-unrendered for non-props; footprints are axis-aligned by design.
decision: 2026-07-16 Add rotation channel — Add a per-entity rotation channel to the building/unit MultiMesh bridges so authored yaw renders.

### DW-153: Marquee selection and 3D selection markers assume flat y=0 terrain

origin: code review of spec-6-6 (doodads/multi-select), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/UI/EntityPlacer.cs (FinishMarquee unprojects at hard-coded heights; GroundPointOf intersects the y=0 plane)
severity: low
reason: Box-select misses entities on Story-6.3 elevated terrain and markers sink below raised ground. The SAME y=0 convention every existing editor tool uses — a pre-existing editor-wide limitation surfaced by the new marquee, not unique to 6.6.
status: open

### DW-154: No single-active right-dock arbitration — Camera/Water/Region/Pathability panels overlap when two are toggled

origin: code review of spec-6-6 (doodads/cameras/water), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15). Confirmed from live use by Alec at the epic-6 retro (2026-07-15).
location: godot/src/CreationSuite/CameraTool.cs / WaterTool.cs / RegionTool.cs / PathabilityTool.cs (all pin PanelContainer at AnchorLeft/Right=1, OffsetLeft=-300, CanvasLayer Layer=5)
severity: medium
reason: Nothing arbitrates a single active tool panel; the pattern is shared with the already-shipped RegionTool/PathabilityTool (6.4/6.5). SCHEDULED: story key 14-6 (epic-6 retro action A3-E6 — single-active-dock arbitration + unified hotkey map) owns the closure.
status: open
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 14-6 (dock arbitration + hotkey map)

### DW-155: "One group op = one undo step" is unit-tested only against a HashSet proxy, not the real EntityPlacer group-op composition

origin: code review of spec-6-6 (doodads/multi-select), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/ProjectChimera.Sim.Tests (EditorHistoryTests.GroupOp_IsOneUndoStep) vs godot/src/UI/EntityPlacer.cs (MoveSelection/PasteClipboard/DeleteSelection)
severity: low
reason: The test exercises EditorHistory.Push/Undo/Redo via a local closure; EntityPlacer is Godot-coupled and Tier-1-excluded, so the real composition is a manual godot-verify surface (per the spec's Verification section). The epic-6 retro's A7-E6 in-engine session covers the observation half; a structural fix would extract the composition Godot-free.
status: open

### DW-156: No ContentPackager .chimera.zip round-trip test for props/cameras/water

origin: code review of spec-6-6 (doodads/cameras/water), 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/ProjectChimera.Sim.Tests (ScenarioDataPropsCamerasWaterTests — JSON layer only) vs ContentPackager
severity: low
reason: The round-trip is proven at the ScenarioSerializer JSON layer one level below the AC's "package/import" clause; ContentPackager writes scenario.json wholesale so the data rides along, but a zip round-trip assertion would exercise the exact AC surface.
status: open
decision: 2026-07-28 correct-course — bundle content-package-import-roundtrip merged into map-package-import-one-path (DW-235; Epic 15, Story 15.6)

### DW-157: Blocking prop/water (and 6.5 painted cells) are not honored by the sim on an F5 Edit→Play re-apply — static PathabilityGrid is built once at boot

origin: code review of spec-6-6 (doodads/cameras/water; Blind F1) + pass-2, 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/Core/ScenarioLoadPhase.cs (sole BuildBlockingFootprintMask/PathabilityGrid.Resolve/SetPathabilityGrid/SetStaticBlocked site) + MainScene ResetToAuthoredStart (reuses the boot grid)
severity: medium
reason: An obstacle added in Edit mode is walked through in Play until a true reload, even though CanonicalModelHash already folds it that session. AC-consistent (the spec scoped "un-stamps on reload") and no desync risk (all peers reload identically), but high authoring-loop friction. Fix: rebuild the static grid from current ScenarioData inside ResetToAuthoredStart/ScenarioApplier.Apply — covers both 6.5 painted and 6.6 prop/water in one place.
status: done 2026-07-16
resolution: already resolved: Resolved by commit 6eb3c36 (story 14-8). MainScene.cs:1633-1657 (ResetToAuthoredStart) now rebuilds the static grid from current ScenarioData via ScenarioApplier.BuildPathabilityGrid and re-injects SetPathabilityGrid + FlowFieldSys.SetStaticBlocked; ScenarioApplier.cs:120 BuildPathabilityGrid is the shared recipe both boot (ScenarioLoadPhase.cs:249) and F5 re-apply use; PathabilityReapplyRebuildTests.cs added.

### DW-158: MapBounds > 128 — blocking footprints beyond the ±128 flow-grid extent alias onto edge cells; validator can false-flag an unrelated start

origin: code review of spec-6-6 (doodads/cameras/water; Blind F4 + Edge), pass-2, 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/Core/PathabilityGrid.cs (StampPropInto/StampWaterInto) + ScenarioValidator (cells via FlowField.WorldToCell, which Math.Clamps to [0, GRID_SIZE-1])
severity: low
reason: Two distinct far-out positions alias to one footprint cell — deterministic (no desync) but semantically wrong. Pre-existing whole-editor convention (shared with 6.5 painted cells and entity positions), unreachable when MapBounds ≤ 128 (the 6.7 size set caps at 128). Fix: reject beyond-±grid coords in the validator, or enforce/document MapBounds ≤ 128. Related: DW-162 (the exact +128 boundary line).
status: open

### DW-159: Prop place/paste/duplicate/group-move have no map-bounds guard (WaterTool guards) — off-map paste persists then fails validation confusingly

origin: code review of spec-6-6 (doodads/cameras/water; Blind F7 + Edge E2), pass-2, 2026-07-14 (epic-6 bmad-loop; normalized 2026-07-15)
location: godot/src/UI/EntityPlacer.cs (PlaceProp/BuildCreate/PasteClipboard/MoveSelection/DuplicateSelection — no ±MapBounds check; contrast WaterTool.CommitDrag)
severity: low
reason: Fail-closed (the validator rejects at next save/F5 — no corruption or determinism impact) but the whole-scenario rejection is a poor authoring experience and inconsistent with the water path. Fix: clamp/reject out-of-bounds creates at place/paste/move time with a status message, mirroring WaterTool.CommitDrag.
status: open

### DW-160: Variable map-size grid generalization — the escalation record (five hardcoded grid systems need one map-size source of truth)

origin: code review of spec-6-7-map-properties-new-map-flow-2-4-start-positions-and-minimap-preview.md (Intent-Alignment auditor — this entry IS the escalation record the epic RISK NOTE directed), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Core/FogOfWarSystem.cs (own 128 constants) + FlowField.WORLD_HALF_INT + PathabilityGrid (fixed 2048-byte persist format) + SpatialHash (±160/32-dim) + Terrain3D/NavMesh (256/±128)
severity: medium
reason: Story 6.7 ships "map size" as authored playable half-extents (Small 80 / Medium 120 / Large 128, `ScenarioData.MapBounds`) inside the FIXED ±128 grid identity, per the epic RISK NOTE. Truly resizing the grids is a determinism-critical refactor: it changes the pathability persist format (invalidating every stored scenario's `pathability_blocked`) and forces re-baselining every CanonicalModelHash/StartStateHash/golden fixture. Requires a dedicated correct-course story parameterizing the four sim grids from a single map-size truth source in lockstep, `GridDimensionConsistencyTests` extended per-size, and an explicit one-time golden re-baseline. Until then the fixed 80/120/128 set is the shipped contract.
status: open
decision: 2026-07-25 Author a correct-course determinism story — Parameterize the four sim grids from one map-size truth source in lockstep, extend GridDimensionConsistencyTests per-size, do a one-time golden re-baseline
decision: 2026-07-25 Author a correct-course determinism story — Parameterize the four sim grids from one map-size truth source in lockstep, extend GridDimensionConsistencyTests per-size, do a one-time golden re-baseline
decision: 2026-07-19 Author a dedicated correct-course determinism story — Parameterize the four sim grids from one map-size truth source in lockstep, extend GridDimensionConsistencyTests per-size, and do a one-time golden re-baseline.
decision: 2026-07-16 Keep open
decision: 2026-07-28 correct-course — filed as Story 15.2 (Epic 15, map-size determinism unification)

### DW-161: Start-position "−" remove is not undoable, and "+" increments the picker count before a backing slot exists

origin: code review of spec-6-7 (map properties; Blind + Edge), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/UI/EntityPlacer.cs (remBtn.Pressed ~:1257 — no _history.Push; addBtn.Pressed ~:1239 — _startSlotCount++ before placement)
severity: low
reason: Removing a slot can't be undone, and undoing an earlier move of a since-removed slot can resurrect it, desyncing the picker's transient count from persisted PlayerSlots. Data-at-rest is covered (review PATCH 2: the placement-that-created-a-slot undo removes it; Save persists only placed slots) — this is interaction polish on a godot-verify surface. Fix: route "−" through EditorHistory (redo=remove, undo=re-add) and defer the count increment until a slot is placed.
status: open

### DW-162: A Large (128) map's +X/+Z boundary line aliases into the last fog/flow/pathability cell

origin: code review of spec-6-7 (map properties; Blind Hunter), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Core/Definitions/MapSizes (MaxHalfExtent == FlowField.WORLD_HALF_INT == 128) + FlowField.WorldToCell clamp
severity: low
reason: Positions exactly on the +128 boundary clamp col/row 128→127. Deterministic, affects only the exact boundary line, same pre-existing WorldToCell clamp convention as DW-158. Fix: give Large a small sub-128 margin, or document the edge as the intended playable ceiling.
status: open
decision: 2026-07-25 Keep open — Defer the contradiction again
decision: 2026-07-25 Keep open — Defer the contradiction again
decision: 2026-07-19 Give Large a sub-128 margin — Reduce Large's MaxHalfExtent below 128 so no playable cell sits on the clamp boundary; requires a golden re-baseline.
decision: 2026-07-16 Keep open
decision: 2026-07-28 correct-course — keep open; +128 ceiling documented as part of Story 15.2

### DW-163: Start-position editor assumes contiguous 0-based slots — a validator-legal non-contiguous set (e.g. {0,3}) drops markers and misroutes toggles/remove

origin: code review of spec-6-7 (map properties; Blind F3, Edge #1/#3, Verification-Gap), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Core/ScenarioLoadPhase.cs (SetupStartPositionBridge sizes by clamp(PlayerSlots.Length,2,4) then guards idx < positions.Length) + godot/src/UI/EntityPlacer.cs (toggles by loop index; "−" removes value _startSlotCount-1)
severity: medium
reason: ScenarioValidator permits any unique in-range slot set (no contiguity/slot-0 rule), so slot value 3 in a length-2 set is silently dropped at load and palette buttons misroute. The normal editor flow keeps slots contiguous — this bites hand-authored/generated maps; edit-time visual + a stale-base edge, not data corruption (review PATCH 2 hardened RemoveStartPosition's sim-base clear). Fix: size markers/toggles by max declared Slot+1 (clamped) and drive toggle identity from PlayerSlots[i].Slot, or add a validator contiguity rule.
status: open

### DW-164: The map Export / New-Map write path never runs a hard Validate() — a failing scenario still writes and ships as a package that won't load

origin: code review of spec-6-7 (map properties; Blind F1/F8 — the story's headline finding), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/CreationSuite/WinConditionPhase.cs (ExportMapPackage/CreateNewMap call only the non-fatal CollectAdvisories, after persisting/packaging)
severity: high
reason: A scenario that fails validation (e.g. content stranded past MapBounds by a map-size shrink, or a slot overflow) is still written to disk and shipped in a .chimera.zip whose manifest hash validates but whose scenario.json hard-fails CheckCoord on reload — a silent, unloadable export. Pre-existing (export never validated), and review PATCH 1 at least surfaces the stranding as an advisory covering all coordinate-bearing collections — but the package is still writable. Fix: call Validate() before SaveToFile/Pack; on failure abort with the located error.
status: done 2026-07-16
resolution: already resolved: WinConditionPhase.cs:238 ExportMapPackage now calls `MapWriteGate.Check(_ctx.Scenario, _ctx.SlotFactionDefs)` and aborts before any disk write; :203 CreateNewMap calls `MapWriteGate.Check(blank)` before SaveToFile. Fixed by commit 8d70a7e (story 14-7-remediation-dw-164).

### DW-165: MapBounds is reflected only in placement/tools/hash — camera pan-limits and NavMesh still cover the full ±128 regardless of chosen size

origin: code review of spec-6-7 (map properties; Intent-Alignment Divergence 1 + Blind F11), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Core/CameraPhase.cs + NavigationPhase (neither reads MapBounds; grep of consumers: validator, Region/Water tools, AI/map-gen, CanonicalModelHash)
severity: low
reason: Picking a smaller size restricts placement but not the runtime camera/nav footprint; the spec's Design-Notes claim ("already wired to camera/NavMesh") was inaccurate. ACs still satisfied (grids COVER the extent; placement bounds + hash differ observably). Fix: wire MapBounds into camera pan-limits (and optionally a per-size NavMesh clamp), or correct the documented expectation.
status: open

### DW-166: Minimap preview renders the live edit-mode World3D — editor gizmos can be captured into preview.png

origin: code review of spec-6-7 (map properties; Blind F5 + Verification-Gap), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/CreationSuite/MinimapPreviewRenderer.cs (renders the caller's World3D, OwnWorld3D=false) + WinConditionPhase.RenderMinimapPreview (invoked during Edit-mode export)
severity: medium
reason: The top-down snapshot consumed by skirmish setup (11.1), the MP lobby (9.7), and the content browser (9.10) can capture start-position flag poles, the placement ghost, and active overlays instead of a clean map image. The packaging round-trip is unit-tested and the render fails safe (null → omitted); the visual quality is a godot-verify surface (epic-6 retro A7-E6 covers the observation). Fix: hide editor-only layers/gizmos for the one-shot render (cull mask or visibility toggle), or render from persistent map content only.
status: open

### DW-167: Economy-spinner edits to an already-placed start slot mutate hash-folded StartCrystal with no EditorHistory push (not undoable)

origin: code review of spec-6-7 (map properties; Blind F4 — surface introduced by pass-1 patch 3), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/UI/EntityPlacer.cs (spin.ValueChanged/crysSpin.ValueChanged → _onStartSlotEconomy → UpdateStartSlotEconomy, no _history.Push; contrast PlaceStartPosition which captures ore/crystal)
severity: medium
reason: An accidental economy edit to a placed slot persists immediately and can't be Ctrl+Z'd, breaking the "every editor mutation is one undo step" contract on a hash-folded value. A correct fix must coalesce spinner edits into single undo entries and refresh the mirror + spinner on undo/redo; needs in-engine verification (godot-verify surface).
status: open

### DW-168: A placed Custom producer shows no in-match train buttons — command-card canProduce/GetProductionUnits are enum-only

origin: code review of spec-6-8-custom-building-placement-thread-an-authored-building-id-through-buildingsystem-scenarioapplier-retire-the-enum-gate.md (Blind Hunter, rated HIGH + Verification-Gap), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/UI/CommandCardSystem.cs (RefreshBuildingCard canProduce ~:322-325 matches only Barracks|ArcheryRange|SiegeWorkshop|Aviary) + godot/src/Economy/BuildingSystem.cs (GetProductionUnit/GetProductionUnits :305/:319 resolve category via enum-only CategoryForBuilding whose default is "Melee")
severity: high
reason: The sim TrainUnit path IS def-aware and tested (CustomProducer_RoutesProduction...), but a custom producer's authored produces_category is unreachable from the UI — "placeable, not operable." Out of 6-8's placement intent (the spec explicitly deferred the sibling worker-build-card). Fix: widen canProduce for a Custom producer with non-empty ProducesCategory AND make GetProductionUnit(s) def-aware via the slot's DefinitionId — both must land together or the card lists the wrong (Melee) roster; verify in-engine. Split from DW-68's closure (epic-6 retro, 2026-07-15).
status: open

### DW-169: Every Custom building gets the fixed 5×3×5 CUSTOM_FOOTPRINT regardless of authored mesh size

origin: code review of spec-6-8 (custom placement; Blind + Edge), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Navigation/NavObstacleManager.cs (FootprintFor ~:868 returns CUSTOM_FOOTPRINT for any non-enum id) vs BuildingBridge (visual sized from the GLB AABB)
severity: medium
reason: A large custom building renders at mesh size but blocks a fixed small box (units clip the visual or collide with empty space). Consistent with existing design (built-ins also use fixed TYPE_SIZE footprints), so not a regression — but authored/mesh-derived footprints are absent for exactly the buildings whose sizes vary. Fix: derive footprints from the def (authored field or mesh AABB) for built-ins and customs alike; verify in-engine.
status: open

### DW-170: Custom buildings cannot be referenced in triggers — validator/director building_type checks stay enum-only

origin: code review of spec-6-8 (custom placement; Blind Hunter), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Core/Definitions/ScenarioValidator.cs (trigger building_type checks) + godot/src/Core/ScenarioDirector.cs (Enum.TryParse<BuildingType>)
severity: medium
reason: A scenario that places a custom building and references it in a trigger condition fails validation wholesale. Deliberately out of 6-8's intent — the trigger DSL is Epic 7 scope. Fix (Epic 7, alongside the 7.x trigger rebuild): extend trigger building resolution to accept authored building-def ids, mirroring the scenario-buildings gate.
status: open

### DW-171: BuildingBridge render buckets freeze at Initialize — a mid-session-authored or third-faction custom building renders invisibly

origin: code review of spec-6-8 (custom placement; Blind + Edge + Verification-Gap), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/UI/BuildingBridge.cs (Initialize builds _bucketOf from p1Def/p2Def + the 5 built-in ids; TryBucket returns false → skip, no draw, no diagnostic)
severity: medium
reason: A validated scenario-apply always has its ids in buckets, but the live "author a new building in the 4.5 editor → place it" loop and >2-faction cases miss — guarded against a throw but silent. Fix: re-discover/append a bucket when an unknown DefinitionId appears (or route unknowns to a shared CUSTOM_FALLBACK bucket) so a placed building always renders; verify in-engine.
status: open

### DW-172: def→BuildingStore.Create stat-threading is hand-copied in PlaceBuildingDirectById and CreateEditorBuilding — the "never hand-copied in a spawn path" class

origin: code review of spec-6-8 (custom placement; Blind + Verification-Gap), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/Economy/BuildingSystem.cs (PlaceBuildingDirectById) + godot/src/UI/EntityPlacer.cs (CreateEditorBuilding) — already diverge cosmetically (ShopStock nullable vs Array.Empty)
severity: medium
reason: Both blocks map a BuildingDefinition's Hp/SupplyBonus/ConstructionTime/shop/revive into Create with the same logic; both currently correct (Create null-coalesces) but the duplication is the exact drift class the A2 single-mapper rule exists to prevent on the unit side. Fix: extract one BuildingStore.CreateFromDefinition(def, pos, faction, id) helper called from both sim and editor placement (also unlocks the DW-173 fix).
status: open

### DW-173: Group-move undo of a building restores identity but not def-derived stats — stale stats if the LIFO slot is reused

origin: code review of spec-6-8 (custom placement; Edge Case Hunter), 2026-07-15 (epic-6 bmad-loop; normalized at the retro sweep)
location: godot/src/UI/EntityPlacer.cs (group-move undo ~:2132 sets Alive/Position/Faction/Type/DefinitionId/timers but not Health/MaxHealth/SupplyBonus/shop/revive)
severity: low
reason: Pre-existing (built-in undo also omitted def-derived stats) and BuildingStore recycling makes it unlikely — but 6-8 makes varied def-resolved stats reachable, so a resurrected building can carry a prior occupant's stats. Fix: restore the full def-derived set on undo by re-resolving from DefinitionId (ideally via the DW-172 CreateFromDefinition helper); verify in-engine.
status: open

### DW-325: A net-negative-MaxHealth modifier (research/item/aura) can drive `EffectiveMaxHealth` to 0 and pin a unit at 0 HP…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-1-remediation-dw-85-suppress-the-maxhealth-research-army-heal-on-re-apply.md`
reason: A net-negative-MaxHealth modifier (research/item/aura) can drive `EffectiveMaxHealth` to 0 and pin a unit at 0 HP while it stays alive — no system raises death from a modifier-driven ceiling collapse. — Evidence: `ModifierSystem.RecomputeEntity` floors `EffectiveMaxHealth` at 0 and `ModifierStore.ApplyStatDeltas`/the new DW-85 `ResearchSystem` restore both `Fixed.Clamp(Health, 0, EffectiveMaxHealth)`, so a 0 ceiling yields a 0-HP-alive "zombie". Pre-existing and shared across every +MaxHealth producer (not caused by DW-85 — the research restore mirrors the long-standing ModifierStore clamp); content-gated (no shipped content authors a net-negative max-health modifier today). Closure: decide whether a modifier-driven `EffectiveMaxHealth == 0` should kill the unit (and where that death is raised), or whether 0-HP-alive is intended for the "modifiers never kill" design.
status: open

### DW-326: Story 14.3's new wizard signature-resolution gate surfaces DW-107's silent-skip-on-invalid-ability behavior as a…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-3-remediation-dw-106-factionvalidator-resolves-signature-hero-descriptor-ids.md`
reason: Story 14.3's new wizard signature-resolution gate surfaces DW-107's silent-skip-on-invalid-ability behavior as a misleading faction-blocking error — a `signature_mechanic_effect_id` naming a real ability whose JSON is temporarily broken reports "does not resolve to any loaded ability", blaming the faction for an ability-file problem. — Evidence: `FactionDefinerPanel.OnFinishPressed` calls `AbilityRegistry.LoadFromDirectory(abilitiesDirAbs)` with no `onSkipped` callback, so any ability file failing `AbilityValidator` is silently dropped (DW-107's root behavior); the newly-added registry-gated signature check in `FactionValidator.ValidateComplete` then reports the dangling id against the faction. Pre-existing silent-skip (DW-107) newly made user-visible through this story's gate. Closure: wire an `onSkipped` callback (e.g. `GD.PushWarning`) at the Panel edge so a skipped ability file is at least logged, and/or resolve DW-107's silent-skip at the `LoadFromDirectory` level. Not fixed here: the Panel edge is Godot-presentation (not headlessly verifiable) and the robust fix belongs with DW-107.
status: open

### DW-327: Client faction "selectable" (boot discovery) and "launchable" (the new Edit→Play gate) now disagree on a dangling…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-4-remediation-dw-97-wire-factionvalidator-validatecomplete-into-the-launch-gate.md`
location: godot/src/Core/Definitions/FactionDefinition.cs:318
reason: Client faction "selectable" (boot discovery) and "launchable" (the new Edit→Play gate) now disagree on a dangling `signature_mechanic_effect_id`: discovery reports the faction as selectable, but pressing Play hard-vetoes it with a signature error the boot console never showed. — Evidence: `FactionDefinition.LoadSelectableFromDirectory` (godot/src/Core/Definitions/FactionDefinition.cs:318) and the boot match-load shadow (godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:337) both call `ValidateComplete(def)` with NO registry, so the signature-resolution check is dormant there; Story 14.4's launch gate threads the real `_abilityRegistry` (godot/src/Core/MainScene.cs:1621), so the same faction that appeared in the boot "selectable" set is blocked at Edit→Play with a `signature_mechanic_effect_id` error. A creator with a typo'd signature id sees it listed as selectable, assigns it, then is blocked with an error they never saw at boot. Deliberately out of Story 14.4's scope (intent = wire ValidateComplete into the launch gate only; threading a registry into the registry-less discovery/shadow paths is a separate change). Closure: either thread the ability registry into `LoadSelectableFromDirectory` (and the boot shadow) so all three client `ValidateComplete` sites agree, or explicitly document the intended shadow-lenient / gate-strict asymmetry at the discovery site.
status: open

### DW-328: No Tier-1/integration test pins the a0c8d51 root mechanism itself — shared-`ScenarioData` instance aliasing where…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-5-remediation-editor-map-save-writes-default-persistence-manifest-absent-round-trips-absent.md`
reason: No Tier-1/integration test pins the a0c8d51 root mechanism itself — shared-`ScenarioData` instance aliasing where a manifest attached to one map's instance bleeds into a different map's save; Story 14.5's all-shipped guard only detects a contaminated file post-commit (CI net), it does not prevent the injection, and the panel save seam (`PersistenceManifestPanel.OnSavePressed`) is Godot-Node-bound and Godot-free-untestable. — Evidence: Both the verification-gap and intent-alignment reviewers (Story 14.5 review pass 2) independently flagged that neither new test drives "manifest on instance A → different map's save reuses instance A"; a local/uncommitted save of a contaminated map shows green until committed + CI-run. Related to DW-10 (editor panels do not rebind their held `ScenarioData` after reload). A godot-verify/integration test that drives the panel save action across a map switch and asserts no manifest bleed would close it.
status: open

### DW-329: Story 14.7's map Export/New-Map hard-validate gate is verified by code-read only — no automated test drives the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-7-remediation-dw-164-map-export-new-map-write-path-hard-validate-before-persist.md`
location: ProjectChimera.Sim.Tests.cs
reason: Story 14.7's map Export/New-Map hard-validate gate is verified by code-read only — no automated test drives the actual write path, so a regression that deletes the abort or reorders the gate below `SaveTerrainBesideScenario` (the first disk write) would reintroduce the DW-164 unloadable-export defect while every Tier-1 test stays green. — Evidence: All three primary reviewers (intent-alignment, verification-gap, blind-hunter) independently flagged that the 5 `MapWriteGateTests` exercise only `MapWriteGate.Check` (the Godot-free decision), never `WinConditionPhase.ExportMapPackage`/`CreateNewMap`. `WinConditionPhase` is a Godot-`Node`-bound `ISetupPhase` (`using Godot;`, `Label`, `ProjectSettings`, async minimap render) the Godot-free Tier-1 assembly cannot instantiate, and `ProjectChimera.Sim.Tests.csproj` has no ProjectReference to `godot.csproj`. The "nothing partial on disk on rejection" property (no terrain region files, no `scenario.json` overwrite, no `.chimera.zip`) is asserted by no test. Closure: a Tier-2 GdUnit4 test that drives the phase against a known-invalid scenario and asserts no artifacts are written, or extract the ordered gate→terrain-save→scenario-save→pack sequence behind Godot-free injectable write-delegate seams a Tier-1 test can drive (assert no delegate fires on a gate-rejecting scenario).
status: open

### DW-330: The Story 14.7 export gate forwards `_ctx.SlotFactionDefs` (declared `= null!` on SceneContext, possibly null or…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-7-remediation-dw-164-map-export-new-map-write-path-hard-validate-before-persist.md`
location: godot/src/Core/Bootstrap/Phases/SceneContext.cs:53
reason: The Story 14.7 export gate forwards `_ctx.SlotFactionDefs` (declared `= null!` on SceneContext, possibly null or stale relative to `_ctx.Scenario`) to `Validate`; for a map with a pre-placed CUSTOM (authored, non-enum) building this can false-block a genuinely loadable map — or, if the defs are stale, false-pass an unloadable one (the DW-164 defect via stale defs). — Evidence: `SceneContext.SlotFactionDefs` (godot/src/Core/Bootstrap/Phases/SceneContext.cs:53) is populated by `ScenarioLoadPhase.ResolveSlotFactionDefs` at load/apply and never re-guaranteed at export time; `ScenarioValidator.IsKnownBuildingType(b.Type, OwnerFactionDef(slotFactionDefs, b.Slot))` (ScenarioValidator.cs:307) resolves a custom building id only when the owner faction's defs are present, else falls back to enum names. Before 14.7 export never validated, so custom-building maps exported freely; the new gate can now reject them if the defs array is null/incomplete. Edge-case-hunter rated this the strongest finding. Closure: resolve the per-slot faction defs fresh at export (mirror `ScenarioLoadPhase.ResolveSlotFactionDefs`), or guard the export gate on a null/stale `SlotFactionDefs` with an actionable "reload before exporting" message.
status: open

### DW-331: Scenario write paths other than Export/New-Map still call `ScenarioSerializer.SaveToFile` without the hard…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-7-remediation-dw-164-map-export-new-map-write-path-hard-validate-before-persist.md`
location: MapGeneratorPanel.cs:254
reason: Scenario write paths other than Export/New-Map still call `ScenarioSerializer.SaveToFile` without the hard `MapWriteGate` — `WinConditionPhase.DoImport` (re-saves an imported scenario after stamping TerrainRef), `MapGeneratorPanel`, and `PersistenceManifestPanel` — so an imported/generated/panel-saved unloadable scenario can still land in the scenarios folder ungated. — Evidence: Blind-hunter + intent-alignment noted DW-164's intent scopes to the Export/New-Map paths only; these three are pre-existing ungated writes (`WinConditionPhase.DoImport` SaveToFile, `MapGeneratorPanel.cs:254`, `PersistenceManifestPanel.cs:~338`). Import is the direct mirror of Export (an externally-authored package whose `scenario.json` hard-fails `CheckCoord` is copied into `res://resources/data/scenarios/`), though import content is also gated at apply/load time by `ScenarioLoadPhase`. Closure (post-14.7, if adopted): route these writes through `MapWriteGate.Check` too, or document the intended reliance on the load-time gate for imported/generated content.
status: open

### DW-332: With `slope_auto_block` on, a TERRAIN edit (e.g
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-14-8-remediation-dw-157-rebuild-static-pathability-grid-on-edit-play-reapply.md`
location: MainScene.cs:1645
reason: With `slope_auto_block` on, a TERRAIN edit (e.g. sculpting a cliff) is not honored on Edit→Play (F5) — the reset re-derives slope-blocked cells from the stale boot `ElevationGrid` while painted/prop/water layers rebuild fresh from the live scenario, so the static PathabilityGrid is internally mixed-freshness; units still block on a flattened cliff and walk up a newly-raised one until a full reload. — Evidence: `MainScene.ResetToAuthoredStart` calls `ScenarioApplier.BuildPathabilityGrid(_ctx.Scenario, _applier.ElevationGrid)` (MainScene.cs:1645); `_applier.ElevationGrid` is the terrain baked at boot (`ScenarioLoadPhase.BuildAndInjectElevationGrid` → `SetElevationGrid`) and is never re-sampled from the edited Terrain3D heightmap on Edit→Play. DW-157's intent scopes explicitly to "6.5 painted cells and 6.6 prop/water"; slope/terrain re-bake was declared out of scope in spec-14-8 (the `elev` input is reused, not re-derived). NOT a cross-peer determinism bug — `ResetToAuthoredStart` is the offline-editor loop only; MP peers all fresh-boot with a re-baked grid, so handshake and SimChecksum stay in parity. Edge-Case + Blind-Hunter, most-corroborated. Closure: on Edit→Play, re-bake the `ElevationGrid` from the current terrain heightmap before `BuildPathabilityGrid` (feed it via `SetElevationGrid`), so the slope layer is as fresh as painted/prop/water.
status: open

### DW-333: The trigger `spawn_unit` fan-out offset (`x + i·2.5` in Fixed) and the `OnDisplayMessage` Fixed→float presentation…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-1-trigger-layer-determinism-prerequisites-ordering-fixed-culture.md`
location: ScenarioDelegateBinder.cs:37
reason: The trigger `spawn_unit` fan-out offset (`x + i·2.5` in Fixed) and the `OnDisplayMessage` Fixed→float presentation conversion in `ScenarioDelegateBinder` are unverified — the binder needs a Godot `SceneContext`, so the Godot-free Sim.Tests suite cannot drive it. — Evidence: `ScenarioDelegateBinder.cs:37` computes the determinism-relevant multi-unit spawn coordinate (feeds `SpawnUnitAt` → sim truth); a wrong `SpawnLateralOffset.Raw` (163840 ≠ 2.5) or broken accumulation would ship with no failing test. `ScenarioDirectorSpawnActionTests` captures `OnSpawnUnit` and so bypasses the binder arithmetic entirely. Fix path: extract the fan-out offset into a Godot-free pure helper the sim suite can assert, or stand up a GdUnit4 integration harness. (Story 7.1 review — verification-gap + blind + edge-case reviewers.)
status: open

### DW-334: `LLMService`'s AI-generated-trigger validation guard (spawn map-bounds reject + `display_message` duration…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-1-trigger-layer-determinism-prerequisites-ordering-fixed-culture.md`
location: LLMService.cs
reason: `LLMService`'s AI-generated-trigger validation guard (spawn map-bounds reject + `display_message` duration auto-fix), adapted to Fixed by Story 7.1, has no test at all — a pre-existing untested surface. — Evidence: `LLMService.cs:~314-321` compares Fixed-vs-Fixed for the out-of-map-bounds spawn reject and auto-fixes `Duration <= Fixed.Zero` to `Fixed.FromInt(4)`; repo-wide search finds zero test references to `LLMService`/`ValidateAction`/`ScenarioContext`, so inverting the bounds check or breaking the auto-fix leaves the whole suite green. Fix path: a Godot-free unit test constructing a `ScenarioContext` and asserting the reject + auto-fix (mirrors how the sim suite tests `ScenarioValidator`). (Story 7.1 review — verification-gap reviewer.)
status: open

### DW-335: `TriggerGraph.ToFlat`/`FromJson` are fail-OPEN on a malformed/arbitrary graph — duplicate node ids silently…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-2-graph-canonical-dsl-ir-foundation-closed-registry-converter-lossless-flat-to-graph-migration.md`
location: TriggerGraph.cs
reason: `TriggerGraph.ToFlat`/`FromJson` are fail-OPEN on a malformed/arbitrary graph — duplicate node ids silently corrupt (`byId[n.Id] = n` last-wins drops a node), dangling exec/data edges are silently skipped, a forked exec port takes first-match only, and an `EffectActionNode` wired mid action-chain silently truncates every downstream action — contradicting the module's fail-closed thesis. — Evidence: `TriggerGraph.cs` `ToFlat` builds `byId` with no uniqueness check and gathers events/conditions via `Where(... byId.ContainsKey)` (drops dangling), and the action walk stops at the first non-`ActionNode`. Unreachable in Story 7.2's LIVE path (the sole caller, `ScenarioDirector.LoadScenario`, only ever lowers `FromFlat`-produced graphs, which are unique-id/acyclic/single-successor and contain no `EffectActionNode`); becomes a live silent-data-loss path once Story 7.3 walks authored graphs directly. Corroborated by blind + edge-case reviewers. Closure belongs to Story 7.7 (the authoritative load-time graph validator, "no escape hatch"): reject non-unique ids, dangling/forked edges, and un-lowerable node placements with located errors before any walk. (A fail-closed acyclic-chain guard was already patched into `ToFlat` in 7.2 to prevent an unbounded-hang DoS; the remaining fail-open lossy cases are deferred here.)
status: done 2026-07-30
resolution: already resolved: GraphStructureGate.cs Evaluate rejects duplicate node ids (:232), dangling/forked edges (:242/244/249/258/265), invoked unconditionally at both load gates (Story 7.7)

### DW-336: The graph edge structs (`ExecEdge`/`DataEdge`) have a weaker fail-closed story than nodes — a `DataEdge` JSON…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-2-graph-canonical-dsl-ir-foundation-closed-registry-converter-lossless-flat-to-graph-migration.md`
location: GraphEdge.cs
reason: The graph edge structs (`ExecEdge`/`DataEdge`) have a weaker fail-closed story than nodes — a `DataEdge` JSON missing `wire` silently deserializes to the enum default `DataWireType.Boolean` (no located reject), and edge objects are not duplicate-key-scanned the way `NodeBaseJsonConverter` scans node objects. — Evidence: `GraphEdge.cs` `DataEdge` deserializes via its `[JsonConstructor]` under `DslJson.Options` (POCO path), so a missing `wire` fills the enum default rather than failing closed; harmless in 7.2 (Boolean is the only wire type) but a real gap once Story 7.4 adds Int/Fixed/… wire types. Blind-hunter finding. Closure: give edges the same fail-closed reading (required `wire` + duplicate-key scan) when the expanded wire vocabulary lands (7.4) or under the 7.7 validator gate.
status: done 2026-07-30
resolution: already resolved: GraphEdge.cs:119-194 DataEdgeJsonConverter (Story 7.7) rejects missing 'wire' (:162), requires all five keys, scans duplicate keys (:170), case-sensitive wire parse (:148)

### DW-337: `ExecEdge`/`DataEdge.GetHashCode` uses `HashCode.Combine` (process-seed-randomized) — a latent determinism trap if…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-2-graph-canonical-dsl-ir-foundation-closed-registry-converter-lossless-flat-to-graph-migration.md`
reason: `ExecEdge`/`DataEdge.GetHashCode` uses `HashCode.Combine` (process-seed-randomized) — a latent determinism trap if any future code places edges in a `HashSet`/`Dictionary` and ENUMERATES it (iteration order would vary across runs); and `TriggerGraph.ToCanonicalJson`'s byte-identity rests on System.Text.Json indentation/number formatting, so it must never be used as a cross-runtime hash source. — Evidence: Ordering everywhere in 7.2 goes through `IComparable.CompareTo` (deterministic), never hashing, so this is safe today; but Story 7.3+ graph editing/dedup could enumerate a hash-based set. Separately, the "byte-identical for structurally-equal graphs" claim is tested only within one runtime — the same cross-runtime-formatting risk class as the project's `CanonicalModelHash` landmine (which folds `Fixed.Raw` with a fixed field order, NOT JSON text). Blind-hunter finding. Closure: if/when the graph feeds the MP start-state handshake, fold `Fixed.Raw` + a fixed field order (not `ToCanonicalJson` output), and make any edge hashing determinism-safe. Directly relevant to the CanonicalModelHash determinism landmine.
status: open

### DW-338: `NodeKinds` (the closed `kind` registry consulted by `NodeBaseJsonConverter`) DUPLICATES `ScenarioValidator`'s…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-2-graph-canonical-dsl-ir-foundation-closed-registry-converter-lossless-flat-to-graph-migration.md`
location: NodeBase.cs
reason: `NodeKinds` (the closed `kind` registry consulted by `NodeBaseJsonConverter`) DUPLICATES `ScenarioValidator`'s private `_triggerEventTypes`/`_conditionTypes`/`_actionTypes` string sets with no cross-check guard, so extending the trigger vocabulary (e.g. Story 7.13) in one place and not the other silently diverges (a valid authored `kind` would be rejected at graph parse). — Evidence: `NodeBase.cs` `NodeKinds` hand-copies the three sets; `ScenarioValidator`'s copies are `private` and cannot be shared. No test asserts the two are equal. Latent in 7.2 (graph JSON is not the on-disk format yet, so the converter is not in the live path). The misleading "read, not modified" comment was corrected to "hand-kept copy" as a 7.2 patch. Blind-hunter finding. Closure: Story 7.7 (validator unification) should make the ECA vocabulary a single shared source of truth or add a test asserting `NodeKinds` == the validator's sets.
status: done 2026-07-30
resolution: already resolved: ScenarioValidator.cs:1387 now aliases NodeKinds sets by reference and NodeKindsLockstepTests asserts the aliasing, replacing hand-copied string sets

### DW-339: `ScenarioDirector.RunEffect` rebuilds the entire SpatialHash on every invocation, so N `run_effect` triggers…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-3-typed-scoped-variables-deterministic-timers-and-verify-to-ship-eca.md`
location: ScenarioDirector.cs
reason: `ScenarioDirector.RunEffect` rebuilds the entire SpatialHash on every invocation, so N `run_effect` triggers firing in one tick cost N full `Rebuild(world)` passes. — Evidence: The new graph-walk executor's `run_effect` path (`ScenarioDirector.cs`, RunEffect) rebuilds the spatial index per call rather than once per tick; deterministic and correct, but O(N·world) per tick and unbounded in the number of run_effect triggers. Surfaced by the Story 7.3 blind-hunter review (perf, low). Closure: rebuild the SpatialHash at most once per tick (or lazily/dirty-flagged) before draining run_effect actions.
status: open
### DW-340: `ScenarioDirector.RunEffect` throws `NotSupportedException` mid-tick if the director was constructed without…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-3-typed-scoped-variables-deterministic-timers-and-verify-to-ship-eca.md`
reason: `ScenarioDirector.RunEffect` throws `NotSupportedException` mid-tick if the director was constructed without `SetEffectRuntime` (no `ModifierStore`) and an embedded effect uses apply_modifier/persistent. — Evidence: `EffectExecutor` throws when a `PersistentEffect`/`ApplyModifierEffect` runs without a `ModifierStore`; production is wired via `SimulationHost.SetEffectRuntime`, but any `ScenarioDirector` built off the `SimulationHost` path (test helpers, future non-host callers) crashes on such an effect. Story 7.3 blind-hunter (low, fragility). Closure: fail-closed at the validator/load gate (reject modifier-bearing trigger effects when no ModifierStore is wired) or make RunEffect degrade deterministically.
status: open
### DW-341: `DslVarTable.FoldInto` folds a Point-typed variable's second component (Raw1/Z) which is never populated or…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-3-typed-scoped-variables-deterministic-timers-and-verify-to-ship-eca.md`
reason: `DslVarTable.FoldInto` folds a Point-typed variable's second component (Raw1/Z) which is never populated or written in Story 7.3, so the fold-coverage teeth cannot catch a future Point write that escapes the checksum. — Evidence: `ScopeInitialRaw`/`SetInt`/`GetInt` touch only Raw0; `ScenarioVariable` has no Point-Z field, so a Point slot's Z is structurally always 0 (harmless, unreachable today). Non-Int typed read/write and Point population land in Stories 7.4/7.6. Story 7.3 verification-gap + blind-hunter (low). Closure: when Point/Array population lands, extend `SimChecksumCoverageGuardTest` fold teeth to a real 2-component Point and assert both components fold.
status: open
### DW-342: The Trigger Editor "Manual (ECA)" form only authors variable read/write, display_message, and run_effect actions —…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-3-typed-scoped-variables-deterministic-timers-and-verify-to-ship-eca.md`
reason: The Trigger Editor "Manual (ECA)" form only authors variable read/write, display_message, and run_effect actions — not the full closed action vocabulary (spawn_unit / create_timer / add_resources / victory / defeat / play_sound), and hardcodes the `variable_comparison` operator to `==`. — Evidence: Story 7.3 delivered the AC3-required variable + run_effect authoring, but the broader FR-23 "basic ECA authoring" surface advertises action kinds the manual form cannot express; the raw-IR hatch is the current escape for them. Story 7.3 blind-hunter (low, UX completeness). Closure: extend the manual form to cover the remaining closed action/operator vocab (or fold into the Story 7.10 T3 visual editor).
status: open
### DW-343: Variable names that collide with expression keywords/built-ins (`true`, `false`, `count`, `min`, …) or contain…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-4-fixed-point-arithmetic-and-boolean-expression-layer.md`
reason: Variable names that collide with expression keywords/built-ins (`true`, `false`, `count`, `min`, …) or contain non-identifier characters are declarable (7.3 name policy accepts any non-empty string) but unreferenceable from CEL-shaped expression text — `true` parses as the Bool literal, not the variable. — Evidence: `ScenarioValidator` accepts any non-empty unique variable name while `ExprParser`'s grammar reads `[A-Za-z_][A-Za-z0-9_]*` identifiers and resolves keywords/built-ins first; raw-IR `expr_var` nodes can still reference such names, so the collision is authoring-surface-only. Story 7.4 edge-case-hunter (low). Closure: either lint/warn at declaration time on names shadowing the expression grammar, or add an escape syntax — a back-compat policy decision (existing scenarios may already carry such names).
status: open
### DW-344: `distance(p, q)` degenerates to |ΔX| for any Point variable loaded from an authored scenario, because…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-4-fixed-point-arithmetic-and-boolean-expression-layer.md`
reason: `distance(p, q)` degenerates to |ΔX| for any Point variable loaded from an authored scenario, because `ScenarioVariable` carries a single `Fixed Initial` (no Z component), so a Point's Raw1/Z is structurally 0 through the real load path. — Evidence: `ScenarioDirector.ScopeInitialRaw` maps a declared Point initial to raw0 only; the two-raw `DslVarDecl` ctor (Z lane) is reachable only from tests. The evaluator's distance math is correct and matches `FixedVec3.Distance` when Z is populated. Extends the existing 7.3 Point-Raw1 ledger entry to the 7.4 built-in surface. Story 7.4 verification-gap + intent-alignment (low). Closure: when Point authoring lands (7.6/later), extend `ScenarioVariable` with a Z initial (schema + serializer byte-identity care) and add an authored-scenario→distance() end-to-end test.
status: open
### DW-345: The Trigger Editor manual form's LEGACY (flat/literal) `set_variable` path offers PerPlayer-scoped Int variables…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-4-fixed-point-arithmetic-and-boolean-expression-layer.md`
reason: The Trigger Editor manual form's LEGACY (flat/literal) `set_variable` path offers PerPlayer-scoped Int variables in its picker but always writes player slot 0 — the flat `TriggerAction.Faction` defaults to 0 and the form exposes no slot picker. — Evidence: `TriggerEditorPanel.RefreshVarPickers`'s non-widened filter is type-only (`v.Type == DslValueType.Int`, any scope) and `OnManualAddPressed`'s flat path never sets `action.Faction`; predates 7.4 (7.3 shipped it) and is unchanged by this story — 7.4's second-pass review closed the same gap on the NEW expression path only (widened picker now excludes PerPlayer). Story 7.4 pass-2 blind-hunter + verification-gap (low, pre-existing). Closure: either exclude PerPlayer from the legacy picker too, or add a player-slot picker to the manual set_variable row (fits the 7.10 T3 editor work).
status: open
### DW-346: The Layer-3 fuel seatbelt meters only chain-side work — trigger condition-expression evaluation, legacy condition…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: The Layer-3 fuel seatbelt meters only chain-side work — trigger condition-expression evaluation, legacy condition checks, and event collection are charged zero ops and trigger count is uncapped, so a scenario with thousands of individually-legal heavy conditions does unbounded per-tick work the seatbelt never sees. — Evidence: Every `Charge` site in `ScenarioDirector` is on the action/drain side, and `DslLoopGate.WalkChain`'s static cost model walks `ex.Items` only, never `ConditionExprRoots` — consistent with the spec's cost formula (which omits conditions) but leaving the per-tick sweep's condition pass unbounded by trigger count. Pre-7.6 exposure (condition expressions shipped in 7.4); surfaced by the 7.6 blind-hunter (medium, seatbelt coverage). Closure: charge condition evaluation (per-expression OpCount) into the same per-tick budget, or cap enabled-trigger count at the gate — either moves `SimChecksum` folds only if fuel values change, so bundle with a planned AlgoVersion bump.
status: open
### DW-347: Fuel charges `run_effect` at its static embedded-node count, so a `SearchAreaEffect` executing its child per…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: Fuel charges `run_effect` at its static embedded-node count, so a `SearchAreaEffect` executing its child per matched target (up to `MaxSearchTargets=64`) does up to ~64x uncharged work — weakest exactly inside entity loops (iterations x search-targets). — Evidence: `CompiledItem.RunEffectCost` = `DslLoopGate.CountEffectNodes` (static), while `EffectExecutor` fans `SearchArea` children out per target; the spec's cost model prescribes "run_effect = embedded node count," so the implementation matches intent but the model undercounts dynamic fan-out. Story 7.6 blind-hunter (medium, seatbelt accuracy). Closure: weight `SearchArea` nodes by `MaxSearchTargets` (worst-case, static — keeps determinism and the load-gate product check aligned) in both the static model and runtime charging; note any charging change forces a golden re-baseline (fuel folds into `SimChecksum`).
status: open
### DW-348: Loop/array nodes not exec-reachable from any trigger chain skip all of `DslLoopGate`'s semantic checks (undeclared…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: Loop/array nodes not exec-reachable from any trigger chain skip all of `DslLoopGate`'s semantic checks (undeclared arrays, bad `up_to`, loop-var rules) and validate silently as inert orphans in the graph. — Evidence: `DslLoopGate.CheckGraph` walks trigger exec chains only; an orphan `for_each` referencing an undeclared array passes both gates (it also never executes, so this is authoring hygiene, not a runtime hazard). Consistent with the story's explicit deferral of dangling/structural checks. Story 7.6 edge-case-hunter (low). Closure: Story 7.7's structural validator should extend its orphan/dangling-node pass to run the same semantic checks over unreachable loop/array nodes.
status: open
### DW-349: Fuel exhaustion and batched-drain suppression silently drop one-shot edge events (`match_start`, `unit_dies`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: Fuel exhaustion and batched-drain suppression silently drop one-shot edge events (`match_start`, `unit_dies` edge-detects) for skipped/suppressed triggers — "re-evaluate next tick" holds only for polled events. — Evidence: `EvaluateTriggers` breaks the sweep on `FuelExhausted` and skips suppressed draining triggers, while `CollectEvents` clears `_firstTick`/`_prevFlags` edge state unconditionally each tick, so an event that fired on a skipped tick is gone forever; every fuel test uses the polled `unit_count_threshold` event, so the loss class is unexercised. Deterministic and spec-consistent ("skip this tick" / "suppressed while draining"), but a silent behavioral drop. Story 7.6 edge-case-hunter + blind-hunter (medium). Closure: when Story 7.5's event queue lands, re-queue unconsumed edge events for fuel-skipped/suppressed triggers (or document the loss as authored semantics in the DSL reference).
status: open
### DW-350: Gate/backstop invocation asymmetry plus silent-drop of stray data edges into the new 7.6 ports — a loop-free graph…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: Gate/backstop invocation asymmetry plus silent-drop of stray data edges into the new 7.6 ports — a loop-free graph with an index-in edge (or a non-expression source into a branch cond-in port) rejects at the validator but loads silently at the `HasLoopConstructs`-guarded backstop; unreachable `spawn_unit` nodes reject at the validator scan but pass the backstop's exec-reachable walk. — Evidence: `ScenarioValidator` runs `DslLoopGate.CheckGraph` unconditionally; `ScenarioDirector.LoadScenario` guards it behind `HasLoopConstructs` (only `CheckSpawnCounts` is unconditional), and the gate's edge scans reject only the enumerated cases, silently ignoring unlisted stray edges the runtime then resolves by canonical order. Sanctioned flow always passes the validator, so exposure is direct/hand-crafted loads (defense-in-depth only). The `DslLoopGate` class doc now states the asymmetry precisely (7.6 review P13). Story 7.6 blind-hunter + edge-case-hunter (low). Closure: Story 7.7's structural validator should cover stray/forked data edges into all 7.6 ports and reconcile the two gates' invocation postures.
status: done 2026-07-30
resolution: already resolved: ScenarioDirector.cs:499/522 GraphStructureGate.Check + DslLoopGate.CheckGraph now run unconditionally (no HasLoopConstructs guard); stray data edges rejected via NodePorts table
### DW-351: Nested `for_each` loops naming the same TriggerLocal `loop_var` load cleanly; the inner loop overwrites the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: Nested `for_each` loops naming the same TriggerLocal `loop_var` load cleanly; the inner loop overwrites the variable, so the outer body's remainder reads the inner loop's last element. — Evidence: `DslLoopGate` checks loop-var declaration/scope/type per node but never cross-node uniqueness along a nesting chain; deterministic but silently confusing authoring. Story 7.6 blind-hunter (low). Closure: add a located reject (or lint) for shadowed loop vars in Story 7.7's structural pass.
status: done 2026-07-30
resolution: already resolved: DslLoopGate.cs:326-329 ActiveLoopVars now rejects nested for_each loop_var shadowing along a nesting chain (closed by Story 7.7)
### DW-352: Entity loops multiply the per-invocation `RunEffect` SpatialHash rebuild (up to 64 rebuilds per loop per tick…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-6-bounded-foreach-foreachbatched-loops-arrays-and-layer-3-fuel.md`
reason: Entity loops multiply the per-invocation `RunEffect` SpatialHash rebuild (up to 64 rebuilds per loop per tick; batched rows more) and the fuel model charges none of it — extends the 7.3 rebuild-per-invocation ledger entry with the 7.6 loop/fuel dimension. — Evidence: `RunEffect` calls `_effectSpatial.Rebuild(world)` per invocation; a `for_each` body reaches it once per iteration, and `run_effect` fuel cost is the static embedded-node count, so the O(world) rebuild is invisible to `MaxDslOpsPerTrigger`/`MaxDslOpsPerTick`. Per-iteration rebuild is semantically correct (mid-loop kills must be visible to later iterations) — any optimization must preserve that. Story 7.6 blind-hunter (low, perf). Closure: dirty-flag the spatial index (rebuild only after world mutations) and document what fuel does not meter.
status: open
### DW-353: Server-brokered matches never run the new HandshakeGate — `DedicatedServer.HandleReady` ignores the Ready packet's…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
location: DedicatedServer.cs:223
reason: Server-brokered matches never run the new HandshakeGate — `DedicatedServer.HandleReady` ignores the Ready packet's scenario-hash payload and broadcasts StartGame unchecked, so the fail-closed hash-0/mismatch lobby posture exists only in the P2P topology. — Evidence: `DedicatedServer.cs:223` takes only `slot` from Ready (never calls `TryReadReady`); `LobbyUi.cs:393` defers start to the server in online mode and starts on StartGame with no hash check; `MainScene.cs:487` documents the server-attested multi-hash handshake as Epic 9 / M5 (D-3) scope. Surfaced by the Verification Gap review layer on Story 7.7; closure = route HandleReady through `HandshakeGate.CheckStart` when Epic 9's attestation lands (note `LoopbackDesyncSelfTest` sends `MakeReady(0)` and will need its own hash).
status: done 2026-07-30
resolution: already resolved: DedicatedServer.cs:549/565 HandleReady now calls TickCommandPacket.TryReadReady + ServerLobbyPolicy.CheckStartStateAgreement, fail-closed HALT on disagreement (Epic 9 9.4/9.7)
### DW-354: MapWriteGate call sites (Export/New-Map from 14.7 and Story 7.7's new MapGeneratorPanel AI-save) run the validator…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
reason: MapWriteGate call sites (Export/New-Map from 14.7 and Story 7.7's new MapGeneratorPanel AI-save) run the validator without slot faction defs, so a map placing authored custom-faction buildings can pass the boot gate yet be refused persistence with an enum-only "unknown BuildingType" reject. — Evidence: `MapWriteGate.Check(_pendingScenario)` passes no `slotFactionDefs` while `ScenarioLoadPhase` threads `_ctx.SlotFactionDefs` into the same validator; posture is pinned deliberate by `MapWriteGateTests.CustomBuilding_WithNullFactionDefs_IsBlocked` (Story 14.7) and unreachable today (LLM generation force-overwrites faction paths to defaults), so this is a latent false-reject class, not a live bug. Surfaced by the Blind Hunter review layer on Story 7.7; closure = thread the editor's resolved slot defs into every MapWriteGate call site.
status: open
### DW-355: The boot-time "invalid scenario → validated fallback substitution" routing in…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
location: MainScene.cs
reason: The boot-time "invalid scenario → validated fallback substitution" routing in `ScenarioLoadPhase`/`MainScene.ResetToAuthoredStart` has no automated test — the fail-closed DECISION is type-enforced (a failed ValidationResult carries no token) but the Godot-side glue is protected only by the recorded in-engine smoke check. — Evidence: `MainScene.cs` and `src/Core/Bootstrap/Phases/**` are `<Compile Remove>`d from the Tier-1 assembly (`SimSources.props`), so no test executes the substitution routing; a regression that boots an empty world instead of the fallback would keep every Tier-1 test green. Surfaced by the Verification Gap + Intent Alignment layers on Story 7.7; closure = a Tier-2 (GdUnit4) boot test driving an invalid scenario file and asserting the fallback world + located toast.
status: open
### DW-356: The v8 CanonicalModelHash COLD compute on a pathological all-caps scenario (~4000-node graph) is ~96 ms median…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
location: MainScene.cs:479-480
reason: The v8 CanonicalModelHash COLD compute on a pathological all-caps scenario (~4000-node graph) is ~96 ms median, dominated (~77 ms) by `TriggerGraph.FromJson`/`NodeBaseJsonConverter` parsing each node into a transient `JsonDocument`; the hash re-parses `TriggerGraphJson` that `ScenarioDirector.LoadScenario` already parses on the same load. — Evidence: `CanonicalModelHashPerfTests` measures cold median ~79–104 ms (dev box). Not a live handshake stall — `MainScene.cs:479-480` computes the wire hash ONCE at apply and caches it; the lobby Ready path compares the cached uint via `HandshakeGate` and never recomputes, and the graph parse is inherent to loading regardless of the hash. Bounded today by a 250 ms one-time-load regression ceiling. Closure = share LoadScenario's single parsed `TriggerGraph` with the hash fold (cache the parsed graph on the applied model / thread it through), or replace `NodeBaseJsonConverter`'s per-node transient-JsonDocument read with a streaming `Utf8JsonReader`, to bring cold under the low-tens-of-ms warm budget. Surfaced by the Blind Hunter, Edge Case Hunter, Verification Gap, and Intent Alignment review layers on Story 7.7.
status: open
### DW-357: `ExecEdge` has no strict JSON converter (unlike Story 7.7's new `DataEdgeJsonConverter`), so a hand-authored…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
reason: `ExecEdge` has no strict JSON converter (unlike Story 7.7's new `DataEdgeJsonConverter`), so a hand-authored `exec_edge` omitting `src`/`dst` silently defaults the missing endpoint to node 0 and passes `GraphStructureGate` mis-wired, rather than being a located parse reject. — Evidence: Only `DataEdgeJsonConverter` is registered in `DslJson`; `ExecEdge` still deserializes via its `[JsonConstructor]` with default 0 endpoints. `GraphStructureGate`'s "every endpoint must exist" check is satisfied because node 0 usually exists, so the malformed edge reroutes the exec chain onto node 0 undetected. Story 7.7 intent named parse-level fail-closed for the data-edge `wire` case only (exec-endpoint strictness is beyond the named scope), and the exposure is direct/hand-crafted JSON — sanctioned editor output always writes full endpoints. Surfaced by the Edge Case Hunter review layer on Story 7.7. Closure = add a symmetric `ExecEdgeJsonConverter` requiring all four endpoint keys (guarding legitimate port defaults), or extend `GraphStructureGate` to reject an exec edge whose omitted endpoint collapsed to 0.
status: open
### DW-358: `GraphStructureGate` rejects forked exec-OUT (two exec edges leaving one `(src,srcPort)`) but not forked exec-IN…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
reason: `GraphStructureGate` rejects forked exec-OUT (two exec edges leaving one `(src,srcPort)`) but not forked exec-IN across triggers — two exec edges entering one node's exec-in port from two different triggers' chains pass the gate, leaving the node executing under multiple owners. — Evidence: The exec-edge loop tracks only forked exec-OUT; the within-one-trigger convergence case is caught downstream by `BuildExecutionOrder`'s per-trigger cycle guard, but a cross-trigger exec-in convergence escapes both. Story 7.7's intent enumerated the reject set as forked exec-OUT + forked data-IN + strays (not forked exec-IN), so this is beyond the named structural scope; reachability requires hand-authored cross-trigger exec wiring. Surfaced by the Edge Case Hunter review layer on Story 7.7. Closure = add an exec-in `(dst,dstPort)` uniqueness check across the whole graph in `GraphStructureGate`.
status: open
### DW-359: `DslLoopGate` rejects a `for_each` `loop_var` that shadows another loop var along a nesting chain (the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
reason: `DslLoopGate` rejects a `for_each` `loop_var` that shadows another loop var along a nesting chain (the 7.6-ledgered case, now closed by 7.7) but NOT a `loop_var` that shadows a declared Global/scenario variable of the same name — the loop binding silently shadows the declared variable inside the body. — Evidence: The shadowing check compares loop-var vs loop-var only; it never consults the declared-variable set (`InitFromDeclarations` names). Deterministic but silently confusing authoring. Story 7.7 intent scoped loop-var shadowing to nested-loop chains, so loop-vs-declared is beyond the named scope. Surfaced by the Edge Case Hunter review layer on Story 7.7. Closure = extend the shadowing reject to also fire when a `loop_var` collides with a declared Global variable name.
status: open
### DW-360: The `LobbyUi` Ready-packet handler wiring that feeds `HandshakeGate.CheckStart` (parsed-flag routing, local/peer…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
location: SimSources.props
reason: The `LobbyUi` Ready-packet handler wiring that feeds `HandshakeGate.CheckStart` (parsed-flag routing, local/peer hash argument slots, and NOT setting `_peerReadyConfirmed` when the gate blocks) has no automated coverage — only the pure `CheckStart` decision is unit-tested. — Evidence: `HandshakeGateTests` exhaustively pins `CheckStart` (incl. the `peerHashParsed:false` block), but `LobbyUi` is not globbed into the Tier-1 assembly (`SimSources.props` includes only `Multiplayer\Server\**` + single-file `HandshakeGate`), so the Godot-side handler is verified only by diff inspection. A regression that passes `peerHashParsed:true` unconditionally, swaps the hash arg slots, or marks the peer ready before consulting the gate would re-open the fail-open start with every test still green. Distinct from the already-ledgered boot/F5 substitution glue entry (that one covers `ScenarioLoadPhase`/`ResetToAuthoredStart`, not the lobby handler). Surfaced by the Verification Gap + Intent Alignment review layers on Story 7.7. Closure = extract the handler's parsed-flag+arg marshalling into a Godot-free helper (as `HandshakeGate`/`FactionLaunchGate` already were) and unit-test it, or add a Tier-2 lobby test.
status: open
### DW-361: Story 7.7 moved `ScenarioApplier.Apply`'s `RevivalRuntime.Configure`/`Resources.ConfigureSupply` calls below the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-7-authoritative-server-side-load-time-validator-gate-no-escape-hatch.md`
reason: Story 7.7 moved `ScenarioApplier.Apply`'s `RevivalRuntime.Configure`/`Resources.ConfigureSupply` calls below the null-model guard (so consuming a failed/default `Validated<ScenarioData>` token is now a pure no-op instead of resetting revival/supply config), but no test pins this documented behavior change. — Evidence: `Apply_MalformedRegions_…` asserts the failed-token apply does not throw and fires no region trigger, but never establishes a non-default revival/supply baseline to observe that a subsequent failed-token apply leaves it unchanged; moving the two `Configure` calls back above the guard (reintroducing the clobber) keeps every test green. Low consequence (the failed-token path is defense-in-depth behind the type-enforced fail-closed guarantee). Surfaced by the Verification Gap review layer on Story 7.7. Closure = a Tier-1 test that configures non-default revival/supply, consumes a `default` token, and asserts both survive.
status: open

### DW-362: The Story 7.8 `CustomUiBuilderPanel` authoring surface is built but unreachable — never instantiated by any…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback.md`
location: godot/src/CreationSuite/CustomUiBuilderPanel.cs
reason: The Story 7.8 `CustomUiBuilderPanel` authoring surface is built but unreachable — never instantiated by any bootstrap phase, editor menu, or hotkey (grep confirms zero external references), so custom widget trees can only be hand-authored in JSON; it also lists all variables in its bind dropdown without type-filtering (its own XML doc claims filtering) and offers no way to nest a widget inside a Panel (only appends roots). — Evidence: `godot/src/CreationSuite/CustomUiBuilderPanel.cs` exists and produces valid trees but has no caller; `AddWidget` only appends to `_tree.Widgets`; `RefreshOption`/`Save` run no type filter and no `CustomUiGate` check, so an author can save a tree that fails the load gate with no in-editor feedback. AC #2's "creator authors via the widget-palette builder" is not demonstrable in-engine. Needs surfacing through the CreationSuite dock-arbitration + unified-hotkey system (spec-14-6), plus a type-filtered bind dropdown, nested-child authoring, and a gate-on-save preflight. Likely matures alongside Story 7.9 (write rail — buttons live in the same widget tree).
status: open

### DW-363: `godot/src/UI/WidgetFormat.cs` (int→string / Fixed→mm:ss / Fraction presentation formatters) has zero unit…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback.md`
location: godot/src/UI/WidgetFormat.cs
reason: `godot/src/UI/WidgetFormat.cs` (int→string / Fixed→mm:ss / Fraction presentation formatters) has zero unit coverage; its `MmSs` (Fixed-as-seconds vs Int-as-ticks, ticksPerSecond division, negative→0:00 clamp), `Fraction` (divide-by-zero guard, [0,1] clamp), and `Number` (Fixed 16.16 trimmed decimal) all carry non-obvious branching. — Evidence: Presentation-only (regression is a visible display bug, never a desync), and it lives in the Godot `src/UI` assembly which the Godot-free Tier-1 `ProjectChimera.Sim.Tests` does not reference, so it is not unit-testable under the current harness. Covering it needs either a presentation-layer test project or relocating the pure helper to a Tier-1-visible location.
status: open

### DW-364: `CustomUiGate` does not bound-check the widget tree's geometry — `CanvasWidth`/`CanvasHeight` and per-widget…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback.md`
location: godot/src/Dsl/CustomUiGate.cs
reason: `CustomUiGate` does not bound-check the widget tree's geometry — `CanvasWidth`/`CanvasHeight` and per-widget `W`/`H` may be zero/negative/absurd and still pass the gate, and those raw values are folded into the MP handshake hash. — Evidence: `godot/src/Dsl/CustomUiGate.cs` validates caps/ids/anchors/binds but no geometry ranges; the renderer defensively defaults `<=0` canvas dims to 1920/1080 and a 0×0 widget simply renders invisibly, so this is author-error hardening rather than a live defect (peers sharing one scenario file cannot diverge). A future hardening pass could reject non-positive canvas dims / degenerate geometry with a located error and enforce the "fixed 16:9 canvas" claim.
status: open

### DW-365: A repeater's `Rows` and a `ProgressBar`'s `Max` have no LOWER-bound gate (only the `Rows > MaxListRows` upper…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback.md`
reason: A repeater's `Rows` and a `ProgressBar`'s `Max` have no LOWER-bound gate (only the `Rows > MaxListRows` upper cap), so an authored `rows:0`/negative or `max:0` passes `CustomUiGate` and is folded into the v9 canonical hash as a distinct value, yet the renderer silently overrides it — the hash claims a semantic difference the runtime erases. — Evidence: `CustomUiGate.CheckWidget` checks only `w.ExpectsArrayBind && w.MaxRows > DslBounds.MaxListRows`; `CanonicalModelHash.MixWidget` folds `Rows`/`Max` raw; but `CustomUiBridge.RebuildRows` does `rowCap = b.Model.MaxRows > 0 ? b.Model.MaxRows : DslBounds.MaxListRows` — so `rows:0` renders up to 64 rows (author intent discarded), and `WidgetFormat.Fraction` returns 0.0 for `max<=0`. Not a desync (peers share one file) and low consequence (weird authored value → default), but hash and render disagree on the meaning of 0/negative. Surfaced by the Blind Hunter + Edge Case Hunter review layers on Story 7.8. Distinct from the general "geometry ranges unchecked" entry above (that covers canvas/W/H invisibility; this is the repeater-cap / progress-denominator fold-vs-render divergence). Closure = add lower-bound gate checks (`Rows >= 1`, `Max >= 1`) with located errors, or make the renderer honor the folded value instead of defaulting.
status: open

### DW-366: The `custom_ui` widget array is fully parsed and allocated into memory BEFORE any cap is enforced —…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback.md`
location: CustomUiGate.cs:62
reason: The `custom_ui` widget array is fully parsed and allocated into memory BEFORE any cap is enforced — `CustomUiGate.Check` (the only `MaxWidgetCount` enforcement) runs after `WidgetBaseJsonConverter` has already materialized the whole `WidgetBase[]`, so a pathological flat array of millions of sibling widgets allocates before rejection. — Evidence: `WidgetBaseJsonConverter.ReadChildren`/root array read builds the full array with no streaming count guard; `CustomUiGate.cs:62` rejects `> MaxWidgetCount=256` only post-materialization. STJ `MaxDepth` bounds nesting depth but NOT breadth. This mirrors the project-wide parse-then-gate posture for every scenario collection (triggers/regions/units are equally unbounded at parse), so it is a latent DoS-surface class for untrusted/AI-gen/shared scenario files rather than a 7.8-specific regression; the handshake hash covers divergence but the file still parses first. Surfaced by the Blind Hunter review layer on Story 7.8. Closure = a streaming element-count ceiling during parse (applied uniformly across scenario collections, not just custom_ui) or an upstream byte/size guard on untrusted scenario input.
status: open

### DW-367: The director's `unit_dies` source (`_prevFlags` Alive-diff) merges a same-tick die→recycle→die on one entity slot…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-7-5-custom-events-define-raise-subscribe-with-acyclic-same-tick-dispatch.md`
location: godot/src/Combat/DamageResolver.cs:97-104
reason: The director's `unit_dies` source (`_prevFlags` Alive-diff) merges a same-tick die→recycle→die on one entity slot into a single death event carrying only the last killer's attribution — the first kill's event and credit are silently lost. — Evidence: Pre-existing mechanism (the diff predates 7.5 and the spec pins it as the `unit_dies` source); `EntityWorld` free-list recycling within one tick makes the interleaving possible in principle; `DamageResolver.KillEntity` (godot/src/Combat/DamageResolver.cs:97-104) writes per-slot SoA that the once-per-tick diff reads, so two deaths on one slot in one tick can only surface once. A per-tick death list (id, killer, faction) recorded at KillEntity would fix it; flagged by the 7.5 edge-case review.
status: open

### DW-174: Follow-up review still recommended for 7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback after the review budget was exhausted
origin: review-budget-followup
source_spec: `spec-7-8-custom-runtime-ui-read-rail-declarative-widget-tree-version-stamped-readback.md`
severity: low
reason: Review budget (2 cycles) was exhausted with the story finalized (status: done, verify green) while the review pass kept recommending an independent follow-up. The work was committed by bmad-loop run 20260716-100752-2040; this entry preserves the lingering follow-up recommendation for a deliberate later review.
status: done 2026-07-27
resolution: closed by human decision: No known defect; the surface has since been safely extended, so the leftover review recommendation can be accepted.
decision: 2026-07-27 Close as accepted — No known defect; the surface has since been safely extended, so the leftover review recommendation can be accepted.

### DW-175: ComputeFileHash is EOL-sensitive and pre-existing CRLF files persist; sibling WriteIndented writers still emit Environment.NewLine on Windows
origin: a4-e3-crlf-root-cause-fix
source_spec: n/a (Windows verification pass 2026-07-17, post-7-5 re-land)
severity: low
reason: The A4-E3 fix normalizes ScenarioSerializer.Serialize output to LF (closing the golden-hash WSL/Windows divergence), but three adjacent exposures were deliberately NOT fixed. (1) ScenarioSerializer.ComputeFileHash hashes raw disk bytes, so content-identical files that differ only in line endings hash differently. LIVE exposure is currently nil — the lobby pre-match guard was migrated off it (MainScene.cs sets LobbyUi.ScenarioHash from CanonicalModelHash.ToWire(Compute(model)), a typed EOL-immune fold; ComputeFileHash is "retired algo-1"), and its sole remaining consumer, ContentPackager Pack/Unpack integrity, records and verifies the hash over the SAME zip-preserved bytes (self-consistent cross-platform). But the API's own doc still advertises cross-peer file comparison, and NetworkCommand.cs:637 (MakeReady doc) STALELY documents scenarioHash = ComputeFileHash — any future re-adoption as a cross-machine compare re-opens the CRLF mismatch. (2) Pre-existing CRLF files exist in the worktree today (godot/resources/data/scenarios/123.json, my-new-map.json — pre-fix editor saves; abilities/aura_guard.json — AbilityEditorPanel save). .gitattributes text=auto eol=lf keeps the INDEX clean LF, so fresh checkouts are LF; the drift is worktree-only, but the next re-save of these files produces a whole-file EOL diff (one-time git noise). (3) Read-only sweep of other WriteIndented serializers: FactionWriter, ItemWriter, FactionDefinerWizardCore, AbilityEditorPanel, TriggerEditorPanel (writes the TriggerGraphJson string — CRLF lands INSIDE scenario string content on Windows; harmless to CanonicalModelHash, whose v8 trigger-graph fold is typed and never hashes JSON bytes), ContentPackager manifest, DslJson, LocalProfileSource, SettingsManager all still emit Environment.NewLine on Windows. None feeds a byte-hash expectation (the ScenarioSerializer golden was the only one — fixed), but FactionWriter's untouched-nodes-verbatim byte-identity contract + the 3-4 "whole-file re-indent git-noise" note are the same EOL class. Closure options: (a) normalize EOLs at hash time inside ComputeFileHash (weakens its raw-byte tamper-check semantics — decide deliberately), (b) one-time re-save/normalize of the 3 CRLF worktree files, (c) a shared LF-writing choke point for all content writers (or JsonSerializerOptions.NewLine on .NET 9), (d) correct the stale NetworkCommand.cs MakeReady doc comment.
status: open
decision: 2026-07-19 Low-risk cleanup only — Correct the stale NetworkCommand.cs:656 doc to reference CanonicalModelHash and one-time re-save/normalize the 3 CRLF files; keep raw-byte hash semantics.

### DW-176: Offline (F5) DslEvent button raises apply at press-time, outside any recorded command stream
origin: 7-9-review-defer
source_spec: `spec-7-9-custom-runtime-ui-write-rail-dsleventcommand-on-lockstep-bus-replay-v2-dsl-event-record-local-only-action-whitelist.md`
severity: low
reason: `LockstepManager.EnqueueDslEvent`'s offline branch applies the raise immediately via `OrderApplier.Apply` at the moment of the button press (mirroring the F5 need for buttons to work single-player), whereas `EnqueueOrder` is a no-op offline. This is a structural inconsistency with the offline-order model: the raise happens at an input-driven point rather than a tick boundary, and — because it never enters a `TickCommandPacket` — it would be ABSENT from a `.chmr` replay if offline playtests are ever recorded. Live exposure is nil today (replay recording is online-only; offline is free-run), so this is a latent inconsistency, not an active defect. Surfaced by the Blind Hunter + Intent Alignment layers on Story 7.9. Closure = route offline DslEvent raises through the same per-tick queue/record path the online path uses (or explicitly document offline raises as non-recordable) if/when offline replay recording is added.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Latent structural inconsistency with no live exposure: offline (F5) DSL raises apply immediately rather than at a tick boundary and would be absent from a .chmr replay, but replay recording is online-only and offline is free-run, so the loss is unreachable. Only meaningful if offline replay recording is added, which is not on the roadmap.

### DW-177: Godot-coupled write-rail resolution (name→index, arg extraction) and offline sink wiring are Tier-1-inexpressible
origin: 7-9-review-defer
source_spec: `spec-7-9-custom-runtime-ui-write-rail-dsleventcommand-on-lockstep-bus-replay-v2-dsl-event-record-local-only-action-whitelist.md`
severity: low
reason: The write rail's presentation glue lives in Godot-coupled code that `godot/SimSources.props` deliberately excludes from the Godot-free Tier-1 xUnit set: `CustomUiBridge` resolves a button's `EventName` to the custom-event registry index and extracts `arg0`/`arg1` from `ArgRaws` before calling `EnqueueDslEvent`, and the DSL sink is wired onto `LockstepManager`/`ReplayPlayer` in the excluded `MatchLifecycleController`/Bootstrap phase. A name→index resolution mismatch, an arg off-by-one, or a dropped sink assignment would not be caught by any automated test — only by an in-engine godot-verify pass. The determinism-critical decisions were extracted to Godot-free seams where feasible (`ButtonWidget.RaisesSimEvent`, `DslEventRateLimit.CanAccept`, `TryEnqueueExternalDslEvent`) and ARE tested; this entry covers the residual Godot-only glue. Surfaced by the Verification Gap layer on Story 7.9. Closure = an in-engine integration harness (a Godot-side test project) OR a manual godot-verify checklist run for a live per-player button/scoreboard (also the story's recommended follow-up).
status: open
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 10.9 (Epic 10 live-verify batch, A5-E9)

### DW-178: 7.9 write-rail in-engine verify checklist — pass-2 confirmed the DW-177 risk class is real (null-lockstep wiring defect found by review, fixed by getter), residual Godot-only surfaces enumerated
origin: 7-9-review-2-defer
source_spec: `spec-7-9-custom-runtime-ui-write-rail-dsleventcommand-on-lockstep-bus-replay-v2-dsl-event-record-local-only-action-whitelist.md`
severity: medium
reason: The follow-up review pass on Story 7.9 CONFIRMED the exact defect class DW-177 warned about: `CustomHudOverlayPhase` captured `SceneContext.Lockstep` by value 13 phases before `MatchLifecycleController` creates it, so the bridge's lockstep handle was permanently null and every event-raising button press was a silent in-engine no-op (online AND offline F5) — invisible to all 2462 Tier-1 tests. Fixed in the pass (late-bound `lockstepGetter`, matching the proven `scenarioGetter`/`localFactionGetter` idiom, plus a `GD.PushWarning` when a raise cannot reach the bus so a dead rail is visible in one playtest). The fix is verified by construction + build only. Residual surfaces still carrying zero executed coverage, for the eventual in-engine godot-verify pass (supplements DW-177; do not close either without it): (1) live press → `EnqueueDslEvent` → exec-tick apply → trigger fires → counter updates; (2) local-action whitelist purity is proven only through the `ButtonWidget.RaisesSimEvent` seam — no test executes `OnButtonPressed`'s local-action switch (a sim call added inside it would pass every suite); (3) the online encode/transit leg (buffered DslEvent order → `TickCommandPacket` → peer `ApplyOrders` with the sender's faction); (4) the builder inspector flows patched this pass (`ApplyInspector` "(none)" normalization + event-less-button refusal) are Godot-coupled and untested.
status: done 2026-07-19
resolution: closed by human decision: Accept the shipped build/construction verification for the remaining legs.
decision: 2026-07-19 Close, accepting construction+build verification — Accept the shipped build/construction verification for the remaining legs.
verified: A2-E7 (2026-07-19, Windows godot-mcp) — **the write rail is CONFIRMED ALIVE in-engine, closing residual (1)**. A generated gate-valid scenario (score var + "buy" event + trigger `score = score + 1` + a Counter bound to `score` + a Button raising "buy") was driven live in a running Play-mode match: pressing the button (emit `pressed` → `OnButtonPressed`) incremented the bound Counter 0→1→3 with the late-bound `_lockstepGetter()` resolving NON-NULL and NO `GD.PushWarning` dead-rail message — the null-lockstep capture-by-value defect is gone. The custom UI also survived an F5 Edit→Play round-trip (3 widgets rebuilt, counter re-seeded to authored 0). **Residuals (2)/(3)/(4) REMAIN OPEN**: (2) local-action-switch execution and (4) builder-inspector flows were not exercised; (3) the online encode/transit leg is a 2-machine item godot-mcp cannot drive offline (LocalFaction is always Player1). Do not close this entry until (2)/(3)/(4) are covered.

### DW-179: T3 node-graph editor has no per-node field/property inline editing — palette-added nodes carry defaults only
origin: 7-10-review-defer
source_spec: `spec-7-10-t3-visual-node-graph-editor-view-additive-over-the-shared-ir.md`
severity: low
reason: Story 7.10's `DslGraphEditorPanel` fully edits graph TOPOLOGY (add node from a curated palette, wire/unwire typed exec+data edges, delete, move-with-persisted-position) but has NO property inspector for a node's payload: a palette-added `ExprLiteralNode` is locked to `Int/0`, `ExprVarNode`/`RaiseEventNode` to `Name=""`, `ActionNode` "display_message" to `Text=""`, etc., and nothing in the panel edits those fields. The story's named editable surface is "typed exec + data wires" + on-node error rendering + positions (all delivered), so this is a scope boundary, not an AC miss — but it means authoring a COMPLETE new construct end-to-end in T3 alone is not yet possible; field values must be set in the T2 editor or the raw-IR hatch. Surfaced by the Blind Hunter + Intent Alignment layers (the "Reading A" unified-authoring reading). Closure = add a selected-node property inspector (reuse the card-editor field-row + `ChimeraValidationBadge` pattern) covering the palette kinds' editable fields, with per-field located validation.
status: open

### DW-180: T3 node-graph editor in-engine godot-verify checklist — Godot-coupled drag/wire/badge interactions carry zero executed coverage
origin: 7-10-review-defer
source_spec: `spec-7-10-t3-visual-node-graph-editor-view-additive-over-the-shared-ir.md`
severity: medium
reason: The determinism/IR-facing seams of Story 7.10 are Godot-free and Tier-1-tested (`NodeEditorAnnotation` position round-trip + cap, `DataWireColorPalette`, `DataWireInference`, `GraphStructureGate.CheckGraphLocated`/`TryValidateNewEdge`, `TriggerGraph.IsGraphOnly`, layout-move hash-neutrality incl. `StartStateHash`), but the `GraphEdit`/`GraphNode` presentation in `DslGraphEditorPanel` is excluded from the `SimSources.props` Tier-1 set and was exercised only by an open/add-node/save smoke test (panel opens, palette adds typed-port nodes, save succeeds, zero editor errors) — NOT each interaction. Residual zero-executed-coverage surfaces for the manual pass: (1) mouse-drag port→port wiring drawn only after `TryValidateNewEdge` passes, and an illegal drag rejected with a status message; (2) drag-off-port disconnect (the `AddValid{Left,Right}DisconnectType` workaround) actually firing `disconnection_request`; (3) `DeleteNodesRequest` removing a node + its incident edges; (4) a located structural error rendering a `ChimeraValidationBadge` on the correct `g<id>` node; (5) data-wire recolor-by-type on the canvas; (6) the T2 "Edit in graph view" fallback row opening the live T3 panel; (7) `Y`-hotkey toggle gated to Edit mode + auto-close on Play. Same class as DW-177/178. Closure = a manual godot-verify checklist run (or a Godot-side integration harness) for a live per-node wire/badge/delete session; this is also the story's recommended follow-up (`followup_review_recommended: true`).
status: open
decision: 2026-07-27 Keep deferred to the Epic 10 live-verification batch
decision: 2026-07-19 Keep open for a human-driven mouse godot-verify pass
verified: A2-E7 (2026-07-19) — NOT closable via the automated harness. godot-mcp `godot_input` has NO absolute-mouse cursor positioning (relative mouse-look only; confirmed in the tool contract + [[godot-mcp-ui-verify-via-signals]]), so the port→port wire DRAG, drag-off-port disconnect, and `GraphEdit` node-move interactions (checklist items 1/2/5) genuinely cannot be driven — they require a human-driven pass with a real mouse. `DeleteNodesRequest`/badge/T2→T3-open (items 3/4/6/7) are signal-driveable but were NOT exercised this session (budget). `followup_review_recommended: true` RETAINED on story 7-10. This entry stays open for the human-driven pass.
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 10.15 (human-driven verify pass, with DW-182)

### DW-181: `TriggerGraph.IsGraphOnlyKind` ↔ `ToFlat` parity is asserted tautologically — a future graph-only kind added to ToFlat but not the predicate would silently drift
origin: 7-10-review-defer
source_spec: `spec-7-10-t3-visual-node-graph-editor-view-additive-over-the-shared-ir.md`
severity: low
reason: `TriggerGraph.IsGraphOnlyKind` (the T2 read-only-fallback detector) hardcodes the graph-only vocabulary (`raise_event`/`custom_event`/`expr_event_param`/`for_each`/`for_each_batched`/`branch`/array-actions) — the same set `ToFlat` fails-closed or drops on — and the `GraphOnlyKind_*` Theory tests just restate those literals; nothing binds the predicate to `ToFlat`'s actual no-flat-form behavior. Verified they match TODAY. But Story 7.13 ("complete the trigger vocabulary") is anticipated to add graph-only kinds; one added to `ToFlat` but not `IsGraphOnlyKind` would make `ContainsGraphOnly` return false, and `TriggerEditorPanel.RefreshGraphOnlyFallbackRows` would show "(no triggers…)" instead of the read-only fallback — silently hiding a non-flat construct from T2 with the suite green. Surfaced by the Verification Gap layer. Closure = a Tier-1 test that derives the expected graph-only set from `ToFlat`'s round-trip behavior (a kind is graph-only iff `ToFlat` throws/drops it) rather than re-listing the predicate's own literals, so the two can never drift.
status: done 2026-07-26
resolution: already resolved: Story 7.13 added TriggerGraphGraphOnlyEquivalenceTests.cs:47-59 ActionKind_IsGraphOnly_IffItHasNoFlatForm — a Theory over NodeKinds.ActionTypes that runs the real TriggerGraph.ToFlat and asserts IsGraphOnlyKind(kind)==!hasFlatForm, deriving the graph-only set from ToFlat behavior instead of restated literals; plus FlatActionKinds_AllSurviveToFlat and the fail-closed throw-side tests. A graph-only kind added to ToFlat but not the predicate now goes RED.

### DW-182: 7.10 follow-up-pass panel behaviors need in-engine verify steps beyond the DW-180 checklist — reopen edit-preservation, invalid/cycle save status, pre-draw cycle+type rejection, T2→T3 occlusion fix
origin: 7-10-review-2-defer
source_spec: `spec-7-10-t3-visual-node-graph-editor-view-additive-over-the-shared-ir.md`
severity: medium
reason: The follow-up review pass on Story 7.10 patched several `DslGraphEditorPanel`/`TriggerEditorPanel` behaviors whose decision logic is Godot-free and Tier-1-tested (`GraphStructureGate.TryValidateNewEdge` cycle rejection, `GraphStructureGate.FindExecCycle`, `DataWireInference.TryInferSourceType`, `NodePaletteFactory` full-union defaults with per-kind serialize→reparse round-trip) but whose PANEL integration is build-verified only and does not appear in the DW-180 checklist (that entry pre-dates these behaviors; it is left unmodified per the orchestrator's ledger rules — do not close either without covering both): (1) hide/show (Y-toggle, Close, Play-mode auto-close) preserving unsaved topology AND pure position drags (`CapturePositions` now runs on hide); (2) saving a structurally-invalid or exec-cyclic graph showing the located danger status ("INVALID … rejected at load"), never a clean "Saved"; (3) a cycle-closing or known-non-Boolean-into-cond-sink wire drag rejected pre-draw with a status message; (4) the T2 "Edit in graph view" button hiding the (higher-CanvasLayer) T2 panel so the T3 editor is not occluded; (5) the external-edit warning when a T2/raw-IR change replaces unsaved T3 edits; (6) the over-cap/non-object `_editor` position warning on save; (7) the T2 fallback rows for an unparseable graph channel and for graph-channel-only-non-graph-only content. Surfaced by the Blind Hunter / Edge Case Hunter / Verification Gap layers on the follow-up pass. Closure = extend the eventual in-engine godot-verify session with these steps (one session can cover DW-180 + this).
status: open
decision: 2026-07-19 Keep open, bundled with the DW-180 human pass
verified: A2-E7 (2026-07-19) — same harness limitation as DW-180: the wire-drag / drag-off-port / node-move steps need absolute-mouse gestures godot-mcp cannot inject, so this could not be discharged automatically. Bundled with DW-180 for one human-driven godot-verify session. Stays open.
decision: 2026-07-28 correct-course — keep open, blocked; filed to Story 10.15 (human-driven verify pass, with DW-180)

### DW-183: Follow-up review still recommended for 7-10-t3-visual-node-graph-editor-view-additive-over-the-shared-ir after the review budget was exhausted
origin: review-budget-followup
source_spec: `spec-7-10-t3-visual-node-graph-editor-view-additive-over-the-shared-ir.md`
severity: low
reason: Review budget (2 cycles) was exhausted with the story finalized (status: done, verify green) while the review pass kept recommending an independent follow-up. The work was committed by bmad-loop run 20260717-190048-cec1; this entry preserves the lingering follow-up recommendation for a deliberate later review.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Leftover automated review-budget recommendation for Story 7.10 (shipped done/green; Epic 7 DONE and its surfaces since extended by 7.9/7.10 without regression). No identified code defect; running another speculative review pass is not a code change.

### DW-184: Assassination/Landmark presets can miss a target death via same-tick, same-faction EntityWorld slot recycle (no entity generation counter)
origin: 7-11-review-defer
source_spec: `spec-7-11-win-condition-preset-templates-t1-sim-layer-winconditionsystem.md`
severity: medium
reason: `WinConditionSystem.EvaluateAssassination`/`EvaluateLandmark` (godot/src/Core/WinConditionSystem.cs:253-270) treat the target as alive when `world.IsAlive(id) && world.FactionOf[id] == targetFaction`. `EntityWorld.Destroy` frees a slot to the LIFO free-list immediately (EntityWorld.cs:1124) and `Create` pops it same-tick (EntityWorld.cs:767), and base entity ids have NO generation counter (EntityWorld.cs:601). So if the designated leader's slot is recycled into a NEW same-faction unit within the same tick, BEFORE WinConditionSystem ticks at index 14, the death is masked and the preset never latches (leader effectively immortal → wrong/no verdict). Currently hard to reach: the only same-faction spawners between the death systems (CombatSystem[7]/ProjectileSystem[8]) and WinConditionSystem[14] are hero revival (HeroXpSystem[9], which is tick-delayed, not same-tick) and unit production (largely completes in ScenarioDirector[15], after the win evaluator). Surfaced independently by the Blind Hunter and Edge Case Hunter layers; the implementation comment acknowledges the cross-tick case but not an intra-tick recycle. Closure = a generation/ABA-safe target identity (mirror the HeroStore packed `(Generation<<8)|slot` handle) or a per-tick "died this tick" death-feed membership check consulted by the win evaluator; becomes live the moment any system spawns same-faction units synchronously between combat resolution and index 14.
status: open

### DW-185: WinConditionSystem built-ins and presets are hard-2-player (P1/P2); a >2-faction scenario resolves wrong or arbitrarily
origin: 7-11-review-defer
source_spec: `spec-7-11-win-condition-preset-templates-t1-sim-layer-winconditionsystem.md`
severity: medium
reason: `WinConditionSystem.EvaluateBuiltin`/`CountBuildingsAlive`/`CountUnitsAlive` (godot/src/Core/WinConditionSystem.cs:152-187) count only `Faction.Player1`/`Player2`, and `OtherFaction` (cs:284-289) returns the first active faction != f, so in a 3-4 player match (FactionRegistry supports up to 8) Player3/Player4 buildings and units are ignored for the built-ins and every preset marks only the lowest other faction as winner — Player3/Player4 receive no verdict and can be alive when victory is declared. Nothing (validator included) blocks a preset on a >2-faction scenario. This is carried forward from the old presentation switch (also P1/P2-only) and is EXPLICITLY out of scope for Story 7.11 per the intent scope note ("Multi-team (>2 faction) free-for-all resolution beyond the existing P1/P2 two-faction assumption is out of scope") — it is owned by Story 7.12 (N-faction victory resolution + sim-owned alliance mask), which depends on 7.11. Logged so 7.12 can consume it and so the new canonical evaluator's 2-faction limit is tracked. Surfaced by the Blind Hunter and Edge Case Hunter layers. Closure = Story 7.12.
status: resolved
resolution: Closed by Story 7.12 (`spec-7-12-n-faction-victory-resolution-and-per-player-elimination.md`). `WinConditionSystem` was generalized from the 2-faction `OtherFaction` assumption to N-faction, team-aware resolution driven by the new sim-owned `AllianceStore` mask (default FFA/teams-of-1, folded into `SimChecksum` at AlgoVersion 19→20 with all per-tick world goldens re-recorded): every `ActiveFactions` member is now evaluated (no `Player1`/`Player2` literals in the evaluation loops), a defeated faction latches `VERDICT_LOST` at a deterministic tick while the match CONTINUES, and victory resolves for the last live team (or the positive-objective team) with a highest-slot double-elimination tie-break that preserves the 7.11 P1+P2→Player2 parity. Built-ins/presets verified headless with 3–4 factions (`NFactionVictoryTests`), the 7.11 2-faction parity tests kept green. Presentation flips a locally-eliminated player to the RevealAll spectator view + non-terminal defeat banner (`MainScene.OnLocalPlayerEliminated`), firing `ShowGameOver` only on full resolution.

### DW-186: Editor surfaces can save a scenario the fail-closed loader then rejects — no save-time validation surface (author lockout class)
origin: 7-11-review-2-defer
source_spec: `spec-7-11-win-condition-preset-templates-t1-sim-layer-winconditionsystem.md`
severity: medium
reason: The editor writes `ScenarioData` to disk without running `ScenarioValidator`, while every load path is fail-closed (`ScenarioLoadPhase.cs:312-315` rejects and applies nothing; `MainScene.cs:1643` re-validates on Edit→Play). So any editor surface that can author an invalid value produces a file that validates clean nowhere until the NEXT boot rejects it — at which point the author's content no longer loads and they must hand-edit JSON to recover. The class predates 7.11 (trigger editor can reference undefined regions/timers, blank variable names, etc. — the located-reject rules at `ScenarioValidator.cs:596-638` all have editor-reachable authoring paths), but 7.11's win-condition picker widens it: an empty KotH `region_id` or an out-of-range preset index can be committed from the picker with no feedback (the 7-11 follow-up review clamped the slot spinner and hardened the validator, but free-text/index params remain uncheckable client-side). Surfaced by the Edge Case Hunter layer on the 7-11 follow-up pass. Closure = one platform-level save-time validation surface (run `ScenarioValidator.Validate` on save and surface the located error in-editor, blocking or warning), covering all authoring surfaces at once rather than per-picker guards.
status: done 2026-07-26
resolution: already resolved: A save-time validation surface now exists: MapWriteGate.Check (MapWriteGate.cs:51 runs new ScenarioValidator().Validate) hard-gates every scenario disk write before mutation — WinConditionPhase.cs:343-355 (ExportMap), :314 (New-Map), MapGeneratorPanel.cs:301 (AI-gen). An empty KotH region_id or out-of-range preset committed from the picker is now caught at save, so content no longer validates-clean-nowhere until the next boot rejects it.

### DW-187: Follow-up review still recommended for 7-11-win-condition-preset-templates-t1-sim-layer-winconditionsystem after the review budget was exhausted
origin: review-budget-followup
source_spec: `spec-7-11-win-condition-preset-templates-t1-sim-layer-winconditionsystem.md`
severity: low
reason: Review budget (2 cycles) was exhausted with the story finalized (status: done, verify green) while the review pass kept recommending an independent follow-up. The work was committed by bmad-loop run 20260717-223404-9791; this entry preserves the lingering follow-up recommendation for a deliberate later review.
status: done 2026-07-28
resolution: closed as accepted (correct-course 2026-07-28) — Leftover automated review-budget recommendation for Story 7.11 (shipped done/green; Epic 7 fully retrospected/DONE). No actionable code defect; the follow-up-review recommendation is a review-loop artifact.

### DW-188: King of the Hill has no last-team-standing/elimination fallback — a mutual-annihilation KotH match never resolves
origin: 7-12-review-defer
source_spec: `spec-7-12-n-faction-victory-resolution-and-per-player-elimination.md`
severity: medium
reason: `WinConditionSystem.SymmetricLoss` returns false for `WinPresetKind.KingOfTheHill` and `ResolveTick` `return`s after the KotH positive-hold-win check WITHOUT falling through to `ApplyLastTeamStanding` (godot/src/Core/WinConditionSystem.cs, step (2) KotH branch). This is deliberate 7.11 hold-race parity (KotH concludes ONLY by a team reaching `hold_ticks`), and the 7.12 spec kept it (KotH is not listed in the loss pass). But now that every OTHER preset resolves a wiped-out board by elimination + last-team-standing, KotH is the lone exception: if every hill-capable unit on every team is destroyed (no team can ever hold the zone), the match hangs unresolved forever with no verdict. Extreme edge — a KotH match normally resolves by hold-time long before total mutual annihilation — and no correctness bug in the intended flow. Surfaced by the Blind Hunter and Edge Case Hunter layers (Edge Case discarded it as by-design parity). Closure = a deliberate KotH elimination/last-team-standing fallback (make fully-wiped factions eligible for total-wipeout elimination in KotH so `ApplyLastTeamStanding` can resolve a mutual-annihilation match), decided as its own change rather than perturbing the KotH hold-race semantics under review time pressure.
status: open
decision: 2026-07-19 Add a guarded KotH last-team-standing fallback — Make fully-wiped factions eligible for total-wipeout elimination in KotH so ApplyLastTeamStanding resolves a mutual-annihilation match, without perturbing the hold-race win path.

### DW-189: ScenarioDirector.OnVictory DSL escape hatch still computes the winner as `1 - a.Faction` (2-faction-only) — a >2-faction authored victory yields a nonsensical winner slot
origin: 7-12-review-2-defer
source_spec: `spec-7-12-n-faction-victory-resolution-and-per-player-elimination.md`
severity: low
reason: The trigger/DSL victory escape hatch computes the winner of a lose-action as `OnVictory?.Invoke(1 - a.Faction)` (godot/src/Core/ScenarioDirector.cs:1576), valid only for faction slots 0/1. Story 7.12 generalized the sim `WinConditionSystem` to N-faction, team-aware resolution but — per the 7.12 intent's explicit "The ScenarioDirector.OnVictory trigger action stays intact as the advanced/T3 escape hatch" boundary — deliberately left this DSL path untouched. So a DSL-authored `victory`/`defeat` action on a slot ≥2 faction in a 3–4-faction scenario computes a winner of `1 - 2 = -1` (or lower), which flows to `ShowGameOver(winnerSlot + 1)` with a nonsensical/negative arg. NOT reachable in 1.0 (offline and MP are both 2 players; `ServerTransport.MAX_PLAYERS = 2`) and out of scope for 7.12 by the intent's own "stays intact" boundary — logged as a pre-existing latent limitation surfaced incidentally by 7.12 enabling N-faction resolution elsewhere. Surfaced by the Verification Gap layer on the 7-12 follow-up review pass. Closure = teach the DSL victory/defeat escape hatch to resolve the winner through the same N-faction team-aware path the built-in resolver now uses (or, minimally, latch a per-faction verdict and let presentation consume it) instead of the 2-faction `1 - a.Faction` complement, when the >2-faction DSL-authored-victory case becomes reachable (Story 9.2/9.15 player-count widening).
status: open

### DW-190: The per-player-elimination presentation path (MainScene) has no headless/automated verification — the story's headline UX deliverable can regress with the whole suite green
origin: 7-12-review-2-defer
source_spec: `spec-7-12-n-faction-victory-resolution-and-per-player-elimination.md`
severity: medium
reason: Story 7.12's user-visible deliverable — a locally-eliminated player flips to `FogBridge.RevealAll` + a non-terminal defeat banner and keeps spectating, with `ShowGameOver` deferred until `WinConditionSystem.IsFullyResolved()` — lives entirely in `MainScene._Process` (godot/src/Core/MainScene.cs win-handling branch) and the new `OnLocalPlayerEliminated()` (MainScene.cs), which are Godot `Node`-coupled and have NO headless test (a repo search finds only comment mentions of `MainScene`/`OnLocalPlayerEliminated`/`_defeatBanner`, never a driver). The sim predicates the branch consumes (per-faction `Verdict` latching, `IsFullyResolved()`) ARE exhaustively covered by `NFactionVictoryTests`, and the branch is verified-by-construction (reuses the proven `FogBridge.RevealAll` spectator pattern) — but nothing pins MainScene's CONSUMPTION of them: if the no-victor gate regressed from `IsFullyResolved()` back to the old `SoleLoserFaction()`/`IsResolved()` semantics, or the elimination branch condition inverted, a locally-eliminated player in a >2-faction match would get the terminal game-over overlay the instant they lost — the exact defect the story exists to prevent — with the full suite still green; only an in-engine `godot-verify` would catch it. Raised by both the Verification Gap and Intent Alignment layers on the 7-12 follow-up review pass; the prior pass rejected it as "unit-untestable by architecture," but the Verification Gap layer identified a concrete closable seam. Closure = extract the presentation decision into a thin pure helper (input: local faction + `WinStateStore` + `IsFullyResolved()` → enum {Continue, EliminateLocal, GameOver(winnerRep)}) that is unit-tested headlessly, and/or a scripted `godot-verify` acceptance scenario checked into the story's manual-check steps (eliminate the local Player1 in a 3-faction offline scenario → assert RevealAll + banner + no game-over, then play to last-team-standing → assert the VICTORY overlay fires once).
status: open
verified: A2-E7 (2026-07-19) — NOT discharged this session. The MainScene elimination-presentation branch consumes sim predicates (`Verdict` latch, `IsFullyResolved()`) that ARE exhaustively Tier-1-covered by `NFactionVictoryTests` (all green in the 2717-pass Windows suite), but the headline UX (local-elimination → RevealAll spectator + non-terminal defeat banner → deferred game-over) needs a >2-faction match played to elimination, which requires real combat play the harness cannot drive deterministically (and offline is 2-player). The closure's proposed thin pure helper (unit-testable) remains the cheapest real fix. Stays open for a focused/human-driven pass.

## From code review of story-7-13 (2026-07-18)

### DW-191: Load-time run_trigger cycle detection (`DslLoopGate.RunCycleDfs`) is unbounded recursion — a very long *acyclic* run_trigger chain recurses as deep as the chain and can StackOverflow at load; the runtime seatbelt (`MaxRunTriggerDepth=16`) has no load-side equivalent
origin: 7-13-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-7-13-complete-the-trigger-vocabulary-expression-state-reads-randomchoice-enable-disable-run-action-leaves-event-breadth.md`
severity: low
reason: `RunCycleDfs` recurses per run-target with no depth guard; there is no max-trigger/max-node structural cap bounding chain length (grep found none). Mirrors the pre-existing `EventDispatchPlan.CycleDfs` (same recursive shape), so a proper fix converts BOTH cycle-DFS sites to an explicit-stack iterative walk (or adds a global trigger-count cap). Deterministic; requires pathological creator content (~thousands of chained triggers) to trigger — hence deferred rather than blocking. Flagged by both the Blind and Edge-Case review layers.
status: open

### DW-192: `unit_damaged` occurrences can be silently dropped under heavy battle load — `DslSimEventFeed.Capacity=512` with deterministic drop-newest, while `DamageResolver`/`ProjectileSystem` push one occurrence per hit + per splash victim, so a large AoE engagement can exceed 512 pushes/tick and stop firing `unit_damaged` triggers past the cap
origin: 7-13-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-7-13-complete-the-trigger-vocabulary-expression-state-reads-randomchoice-enable-disable-run-action-leaves-event-breadth.md`
severity: low
reason: Deterministic (drop-newest, identical on every peer → no desync), but the cap is low enough that the loss is normal-case behavior in mass combat, not a pathological edge. A designer/tuning decision: raise the cap, or accept documented saturation. Flagged by the Blind review layer.
status: open
decision: 2026-07-25 Raise the cap — Increase DslSimEventFeed.Capacity to cover worst-case AoE ticks (with a memory/cost note)
decision: 2026-07-25 Raise the cap — Increase DslSimEventFeed.Capacity to cover worst-case AoE ticks (with a memory/cost note)
decision: 2026-07-19 Keep open as a tuning item

### DW-193: No direct test pins the `ClearForReset` → re-apply of a *trigger-carrying* scenario re-seeding `TriggerEnabledStore` non-additively; the re-baseline differential guard only proves the zero-trigger fold
origin: 7-13-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-7-13-complete-the-trigger-vocabulary-expression-state-reads-randomchoice-enable-disable-run-action-leaves-event-breadth.md`
severity: low
reason: Low impact — `LoadScenario` calls `_triggerEnabled.Reset(execs.Count)` which fully overwrites the buffer, so omitting the `ClearForReset` `Clear()` would not corrupt a re-apply; the only exposed window is a checksum computed between `ClearForReset` and the next `LoadScenario`, which per project memory is the offline Edit→Play path (not an MP desync path). A cheap regression test (reset → re-apply a disable-carrying scenario → assert the mask re-seeds) would close it. Flagged by the Verification-Gap review layer.
status: done 2026-07-19
resolution: resolved by sweep bundle dw-reset-determinism-test-coverage

## From code review of story-7-14 (2026-07-18)

### DW-194: Several `CanonicalModelHash` AlgoVersion test-method names are stale/misleading — e.g. `AlgoVersion_IsTwelve()` and `AlgoVersion_Pinned_At12()` now assert 14, and `SimChecksumAlgoVersion_Stays20()` asserts 21 — so a reader trusting the name misreads the pinned version
origin: 7-14-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-7-14-objectives-quest-log-and-the-match-briefing-surface.md`
severity: low
reason: Pre-existing rot (the names already drifted before 7.14 across `CanonicalModelHashTests`, `CanonicalModelHashButtonFoldTests`, `CanonicalModelHashCustomUiTests`, `CanonicalModelHashPathabilityTests`, `CanonicalModelHashPropsWaterTests`, `CanonicalModelHashWinConditionFoldTests`); 7.14 updated their assertions to 14 but not the names, perpetuating the mismatch. Not caused by this story (surfaced incidentally by the 13→14 bump); low impact (assertions are correct, only names mislead). Flagged by the Blind review layer. Closure = rename each to a version-neutral form (e.g. `AlgoVersion_IsPinned`) or to the current integer, in one mechanical sweep.
status: open

### DW-195: Dedicated graph-only leaf node kinds are exposed in the T3 palette but the visual graph editor (`DslGraphEditorPanel.PortsOf`) renders no exec ports for them and offers no field editor for their target field, so a node dragged from the palette is unwireable and its placeholder target can't be edited — the map then fails validation and can't be saved
origin: 7-14-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-7-14-objectives-quest-log-and-the-match-briefing-surface.md`
severity: low
reason: `DslGraphEditorPanel.PortsOf` (src/CreationSuite/DslGraphEditorPanel.cs:702-768) has cases only for the generic `ActionNode`/`EffectActionNode`/`RaiseEventNode`/`ForEach*`/`Branch*`/expr families — it has NO case for any dedicated-leaf class, so `ShowObjectiveNode`/`CompleteObjectiveNode`/`FailObjectiveNode` (7.14) AND the pre-existing 7.13-and-earlier leaves (`OrderUnitsNode`/`MoveCameraNode`/`CinematicModeNode`/`PlayVfxNode`/`RandomChoiceNode`/`EnableTriggerNode`/`DisableTriggerNode`/`RunTriggerNode`) all fall through to zero ports. There is likewise no `objective_id`/target field editor. This is a PRE-EXISTING, class-wide editor limitation: 7.14's objective nodes are at exact parity with the accepted 7.13 precedent (NodePaletteFactory exposes them, PortsOf doesn't render them), and the 7.13/7.14 add-a-kind checklist never listed `PortsOf` as a surface. Not caused by this story; surfaced incidentally by the Blind Hunter layer (which flagged the parity itself). Closure = a holistic pass giving every dedicated-leaf kind an exec-in/exec-out `PortsOf` case (derivable from `NodePorts`) plus a target-field inspector, or an explicit decision that these leaves are authored only via the raw-IR/text path and should be removed from the visual palette.
status: open
decision: 2026-07-19 Add PortsOf cases + a target-field inspector for all dedicated-leaf kinds — Give every dedicated-leaf kind an exec-in/exec-out PortsOf case (derivable from NodePorts) plus a target-field inspector, pairing with the DW-179 inspector work.

## From follow-up code review of dw-reset-determinism-test-coverage (2026-07-19)

### DW-196: The DW-19 exhaustive-sweep technique is applied to `EntityWorld` only — the ~20 sibling stores `ClearForReset` wipes are still hand-enumerated, and dropped `Array.Clear` calls in `ProjectileStore`/`HeroStore`/`ItemStore` ship GREEN through the entire suite
origin: dw-reset-determinism-test-coverage-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-reset-determinism-test-coverage.md`
severity: high
reason: DW-19 closed the "new field, forgotten in Clear(), silent pass" blind spot for `EntityWorld` via a reflection sweep, but `SimulationHost.ClearForReset` (src/Core/Sim/SimulationHost.cs:342-371) is a flat fan-out over ~24 stores whose own `Clear()` methods remain verified by hand-enumerated assertions — i.e. they retain the defect class in full. DEMONSTRATED by four independent mutants run against the full 2753-test suite in an isolated worktree, each shipping **2752 passed / 0 failed**: `ProjectileStore.Clear()` minus `Array.Clear(SourceId)`; minus `Array.Clear(Speed)`; `HeroStore.Clear()` minus `Array.Clear(Level)`+`Array.Clear(Xp)`; `ItemStore.Clear()` minus `Array.Clear(DefId)`+`Array.Clear(Charges)`. Severity is high because `HeroStore.Level`/`Xp` and `ItemStore.DefId`/`Charges` ARE folded into the checksum (v12/v13), so an omission there is a live "reset != fresh boot" desync leak that ships green; `ProjectileStore`'s fields are unfolded, so an omission there can never be surfaced by any checksum comparison. Not caused by this story — the intent scoped DW-19 explicitly to `EntityWorld`'s ~70 SoA arrays — and surfaced concurrently by three of four review layers. Closure = lift `DivergingFields`/`NonDefaultValue`/`SyntheticallyFillArrays` out of `EntityWorldClearCompletenessTests` into a shared type-agnostic helper and drive it from an xUnit `[Theory]`/`MemberData` list of (store type, dirty-fixture) pairs over every store `ClearForReset` touches. The machinery is already written and already type-agnostic; this is mostly a lift-and-parameterize.
status: open

### DW-197: `TriggerEnabledStore` never shrinks its `_enabled` buffer and `IsEnabled` returns `true` out of range, so re-applying a scenario with FEWER triggers than the previous one leaves a stale enabled tail
origin: dw-reset-determinism-test-coverage-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-reset-determinism-test-coverage.md`
severity: medium
reason: `Clear()` (src/Core/TriggerEnabledStore.cs:69) zeroes only `Count`; `Reset(count)` re-seeds only `[0, count)` and never shrinks or wipes the tail past `count`; `IsEnabled` (src/Core/TriggerEnabledStore.cs:64-65) returns **true** for an out-of-range index. The new DW-193 test re-applies the SAME 3-trigger scenario, so the stale tail is always fully overwritten and the shrink path is never exercised. The realistic Edit→Play case is precisely the shrinking one — an author deletes a trigger and re-plays — after which a stale `_enabled[2]` remains set behind a smaller `Count`. Whether that is reachable as an observable defect depends on whether any caller reads `IsEnabled` at an index >= `Count`; that reachability question is the first step of closure, not an assumed bug. Not caused by this story (pre-existing production behavior; the intent's matrix specifies only same-scenario re-apply). Flagged independently by the Edge Case Hunter and Blind Hunter layers. Closure = establish reachability, then either bound `IsEnabled` by `Count` (returning false out of range) or have `Reset`/`Clear` wipe the tail, plus a shrink-path regression test.
status: open

### DW-198: The DW-20 fighting-reset test asserts its fight preconditions against the pre-reset host only, so a fixture that goes vacuous exclusively on the re-apply path would leave all three checksum sequences trivially agreeing
origin: dw-reset-determinism-test-coverage-review-defer
source_spec: `_bmad-output/implementation-artifacts/spec-reset-determinism-test-coverage.md`
severity: low
reason: `ClearAndReapply_ReproducesByteIdenticalRun_OverAFightingScenario` asserts in-flight/landed/cooldown/modifier/damage against `host` before `ClearForReset`, but never re-checks them for `run2` (the post-reset re-populate) or `host0` (the independent fresh boot). If a future change made `CombatResetScenario.Populate` produce an inert start state only on the re-apply path, run1 would still be a real fight while run2/run0 quietly became empty — and because all three are compared only against each other, the test would stay green while proving nothing about the reset. Low severity: this is a second-order weakening of a guard that is itself a forward-looking tripwire, and the pre-reset teeth do cover the fixture's main degradation mode. Not caused by this story in the sense that the guard is new and green; surfaced by the Blind Hunter layer. Closure = extract the precondition block into a helper and invoke it against each of the three hosts after their runs.
status: open

### DW-368: Story 8.2 should store LLM API keys per-provider (e.g
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-1-provider-config-isecretstore-provider-model-baseurl-in-versioned-settingsdata.md`
reason: Story 8.2 should store LLM API keys per-provider (e.g. anthropic.key vs openrouter.key) rather than reusing one shared `llm` secret id across all providers. — Evidence: 8.1 seeds/reads a single `SecretIds.Llm` ("llm") key, but the provider is now user-selectable (anthropic/ollama/openrouter). When 8.2's ILLMProvider consumes the selected provider, reusing one `llm.key` would send e.g. an Anthropic key to the OpenRouter endpoint. The store already supports arbitrary ids, so 8.2 can key secrets per provider — flagged so 8.2 doesn't inherit a cross-provider key mismatch. (Blind Hunter, Story 8.1 review.)
status: open

### DW-369: The Godot-coupled bootstrap key-resolution wiring (SettingsPhase constructs the store →…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-1-provider-config-isecretstore-provider-model-baseurl-in-versioned-settingsdata.md`
location: SimSources.props
reason: The Godot-coupled bootstrap key-resolution wiring (SettingsPhase constructs the store → TriggerEditorPhase/ContentBrowserPhase read it) has no runtime/in-engine smoke verifying a stored key actually reaches LLMService/ModIoService. — Evidence: SimSources.props excludes Bootstrap/Phases/**; the phases are compiled out of the Tier-1 harness, so no unit test observes that SettingsPhase runs before the consumers or that `_ctx.SecretStore` is non-null when read. 8.1 mitigated the highest-risk part (id typos) by extracting `SecretIds` + a Tier-1 constant test, but the phase→service delivery remains unit-untestable. Aligns with the A6-E7 production-wiring-smoke standing checklist; a godot-verify/in-engine smoke should confirm a key set via ISecretStore.Set is honored by the Trigger Editor / mod.io path. (Blind Hunter + Verification-Gap, Story 8.1 review.)
status: open

### DW-370: LAN-hosted Ollama (a non-loopback private-range host, e.g
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-2-godot-free-illmprovider-anthropic-ollama-openrouter-four-state-availability-ui-test-connection.md`
reason: LAN-hosted Ollama (a non-loopback private-range host, e.g. http://192.168.1.5:11434) is rejected by the loopback-only allowlist; consider widening the ollama policy to private/RFC-1918 ranges (or naming the loopback-only restriction in the unavailable message). — Evidence: `LlmHostAllowlist.IsAllowed` permits ollama only when `endpoint.IsLoopback`; running Ollama on another box on the LAN is a common setup and is rejected as NoProvider. Story 8.2 deliberately scoped "local Ollama" to loopback; after the 8.2 fix that routes EvaluateConfig through the factory, such a config now honestly shows unavailable rather than a false "ready", but the host itself remains unreachable through the stack. (Blind Hunter, Story 8.2 review.)
status: open

### DW-371: A real `ScenarioType`/`GameMode` registry — an enum + per-type map-clamp preset table (min player slots, max…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-3-ai-generation-on-the-provider-stack-triggers-new-dsl-constructs-maps.md`
location: godot/src/AI/LLMService.cs
reason: A real `ScenarioType`/`GameMode` registry — an enum + per-type map-clamp preset table (min player slots, max combat units/slot, faction-path resolution) + a scenario-type selection UI that populates `MapGeneratorContext` — is deferred; Story 8.3 shipped only the trusted-context clamp mechanism with RTS-preserving defaults. — Evidence: The Epic 8 map-clamp requirement wants clamps relaxed "so non-RTS scenario types are not wrongly rejected," but no story defines a scenario-type schema, and the epic explicitly forbids sourcing relaxed limits from the untrusted scenario file (circular validation) and says to flag it for a decision. 8.3 parameterized the three RTS clamps (`MinPlayerSlots`=2, `MaxCombatUnitsPerSlot`=6, per-slot `FactionJsonResolver`) onto the TRUSTED `MapGeneratorContext` (godot/src/AI/LLMService.cs) with defaults that reproduce today's RTS behavior byte-for-byte, and proved the slice (relaxed context ⇒ previously-rejected scenario passes; default context ⇒ identical behavior; universal position/spacing/bounds passes always run). Closure = add a `ScenarioType` enum + per-type preset table + a scenario-type selection UI in the Map Generator that populates the context's clamp fields per selected type. No persisted `ScenarioType`/`MapType`/`GameMode` schema was introduced (per the spec's Never constraints). (Story 8.3.)
status: open

### DW-372: The map-clamp parameterization is only half-applied for non-2-slot scenarios — the map-gen prompt's SCHEMA and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-3-ai-generation-on-the-provider-stack-triggers-new-dsl-constructs-maps.md`
location: godot/src/AI/LLMService.cs
reason: The map-clamp parameterization is only half-applied for non-2-slot scenarios — the map-gen prompt's SCHEMA and EXAMPLE blocks still hardcode exactly two player_slots, the default `FactionJsonResolver` collapses every slot >= 1 to `Slot1FactionJson`, and the new `MinPlayerSlots`/`MaxCombatUnitsPerSlot` ints have no lower-bound guard. — Evidence: `LLMService.BuildMapSystemPrompt` parameterizes only the one-line "at least {N} slots" placement rule (godot/src/AI/LLMService.cs:~639); the schema/example still show 2 slots, so a caller setting `MinPlayerSlots>2` would be told "at least 4" but shown a 2-slot example and emit 2 -> rejected. `MapGeneratorContext.ResolveFactionJson` defaults to `slot==0?Slot0:Slot1` (correct only for 2-slot RTS; slots 1/2/3 all share Slot1 with no warning when a caller relaxes slots without supplying a resolver). `MinPlayerSlots=0` admits an empty `player_slots` (downstream faction/spawn assumes >=1 player); `MaxCombatUnitsPerSlot<0` emits a "max -1" message. None is reachable today (the sole caller `MapGeneratorPhase` supplies RTS defaults; no non-RTS caller is wired). These are the completeness items the deferred `ScenarioType`/non-RTS-caller work must finish: parameterize the prompt schema/example per slot count, make the per-slot resolver total, and lower-bound-validate the clamps (effective min 1 player slot). (Blind Hunter + Edge Case Hunter, Story 8.3 review.)
status: open

### DW-373: `LLMService.ValidateScenario` never checks player-slot indices are unique or in range — an (untrusted) scenario…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-3-ai-generation-on-the-provider-stack-triggers-new-dsl-constructs-maps.md`
location: godot/src/AI/LLMService.cs
reason: `LLMService.ValidateScenario` never checks player-slot indices are unique or in range — an (untrusted) scenario declaring two slots both "slot":0 passes the length-based min-slots check, both resolve to the same faction, and Pass 7 merges their combat counts under key 0, yielding a degenerate one-faction scenario past the gate. — Evidence: Pass 2 checks only `PlayerSlots.Length >= MinPlayerSlots` then indexes faction-JSON and (Pass 7) combat counts by `slot.Slot` (godot/src/AI/LLMService.cs:~529). Pre-existing on the RTS path (the old code had the identical length-only check and slot-keyed combat count); the "min player slots" name implies a player-count guarantee a length check does not provide, and relaxing the clamp widens the hole. Closure = validate slot indices are distinct and within [0, PlayerSlots.Length) (or [0, MinPlayerSlots)) before the faction-path/combat-count passes. (Blind Hunter, Story 8.3 review; pre-existing.)
status: open

### DW-374: The trigger-gen prompt now advertises `player_chat` (required by the `NodeKinds`-driven staleness guard, since it…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-3-ai-generation-on-the-provider-stack-triggers-new-dsl-constructs-maps.md`
location: NodeKinds.cs
reason: The trigger-gen prompt now advertises `player_chat` (required by the `NodeKinds`-driven staleness guard, since it is a member of `EventTypes`) and `Validate` accepts it, but the sim never raises `player_chat`, so an authored trigger keyed on it validates/saves/loads clean then silently never fires. — Evidence: `NodeKinds.cs:~610` notes `player_chat`'s "raise wire is a later commit"; Story 7.13 registered the event kind but did not wire its raise. 8.3 honestly enumerates the flat registry in the prompt (that is what the staleness guard enforces), which surfaces the not-yet-wired event to end users. Pre-existing DSL gap, not introduced by 8.3. Closure = wire the `player_chat` raise in the sim event source (or remove it from `NodeKinds.EventTypes` until wired, which the staleness guard would then track). (Blind Hunter, Story 8.3 review; pre-existing.)
status: open

### DW-375: `LLMService` is not `IDisposable` — its owned `HttpClient`/`HttpClientHandler` (built on the `http: null` path)…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-3-ai-generation-on-the-provider-stack-triggers-new-dsl-constructs-maps.md`
location: godot/src/AI/LLMService.cs
reason: `LLMService` is not `IDisposable` — its owned `HttpClient`/`HttpClientHandler` (built on the `http: null` path) and the per-call `_cts`/`_mapCts` `CancellationTokenSource`es are never disposed; each Generate press allocates and abandons a CTS. — Evidence: The per-call CTS reassign-without-dispose pattern (godot/src/AI/LLMService.cs:~152/~445) predates 8.3, but 8.3 added an owned `HttpClient`+`HttpClientHandler` that is likewise never disposed (`LLMService` has no `Dispose`). Low impact (the service is a long-lived bootstrap singleton; leak is bounded by generate frequency), but noted since the repoint touched both generate methods. Closure = make `LLMService` `IDisposable`, dispose the prior CTS on reassign and the owned client on dispose. (Blind Hunter, Story 8.3 review; largely pre-existing.)
status: open

### DW-376: `FactionDefinerPanel.OnAiDraftComplete` overwrites the whole in-progress wizard `_draft` with the generated…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-4-ai-entity-drafts-as-editable-data-unit-ability-hero-faction.md`
location: godot/src/CreationSuite/FactionDefinerPanel.cs
reason: `FactionDefinerPanel.OnAiDraftComplete` overwrites the whole in-progress wizard `_draft` with the generated faction (`_draft = def`) and jumps to the Advanced pane with NO undo entry and NO confirmation — unlike the unit path, which wraps its insert in `PushHistory`. An accidental Generate on a populated wizard destroys manual progress irreversibly. — Evidence: `godot/src/CreationSuite/FactionDefinerPanel.cs` OnAiDraftComplete does `_draft = def;` with no history push (the faction wizard has no undo stack today), vs `UnitCardPanel.OnAiDraftComplete`'s `PushHistory(redo/undo)`. Closure = gate the overwrite behind a confirm when `_draft` has meaningful content, or add a wizard-level undo. (Blind Hunter, Story 8.4 review.)
status: open

### DW-377: The Story 8.4 per-kind cancellation apparatus is dead/latent: `LLMService.CancelDrafts()` is defined but never…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-4-ai-entity-drafts-as-editable-data-unit-ability-hero-faction.md`
location: godot/src/AI/LLMService.cs
reason: The Story 8.4 per-kind cancellation apparatus is dead/latent: `LLMService.CancelDrafts()` is defined but never called; `RunDraftAsync` swallows `OperationCanceledException` with no callback, so `SetAiBusy(false)` never runs on cancel — the moment cancellation is wired (e.g. cancel-on-Close) the spinner sticks and Generate stays disabled on reopen. Separately, panels drain events unconditionally, so a late callback after Close still mutates a hidden panel. — Evidence: `godot/src/AI/LLMService.cs` RunDraftAsync `catch (OperationCanceledException) { }` (no enqueue); `CancelDrafts()` has zero call sites (grep src+tests). Panels call `_llm?.DrainEvents()` every `_Process` with no Close/visibility guard. Masked today only because the Generate button is disabled while busy. Closure = enqueue a busy-reset on cancel, wire `CancelDrafts()` on panel Close, and guard the drain/landing against a closed panel. (Blind Hunter + Edge Case Hunter, Story 8.4 review.)
status: open

### DW-378: The Story 8.4 AI-draft UI wiring seams have no automated coverage — the `ChimeraSpinner`/"Transmuting…" toggle…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-4-ai-entity-drafts-as-editable-data-unit-ability-hero-faction.md`
reason: The Story 8.4 AI-draft UI wiring seams have no automated coverage — the `ChimeraSpinner`/"Transmuting…" toggle, the four-state availability line + hide-on-null-deps, the faction `_draft`→Advanced-pane population, the ability `LoadFromRegistry` landing, the `Initialize`-before-`_Ready` ordering, and the unit-ctx's omitted `ItemRegistry` (shop-item refs skip validation at draft). These are the "wiring seams untested behind a green suite" hazard class. — Evidence: All EntityDraft tests stop at the `(def, err)` callback; no test constructs a panel node (Godot-free test project). `UnitCardPanel.OnAiGeneratePressed` builds `UnitDraftContext { AbilityRegistry, BehaviorRegistry }` with no `ItemRegistry`. Closure = extract the draft-landing logic per panel into Godot-free helpers and test them (as `MakeUniqueId`/`UnitCardText` already are), and pass `ItemRegistry` into the unit ctx. (Verification Gap + Blind Hunter, Story 8.4 review.)
status: open

### DW-379: The three panels reimplement near-identical AI-card glue (`BuildAiCard`, `RefreshAvailability`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-4-ai-entity-drafts-as-editable-data-unit-ability-hero-faction.md`
reason: The three panels reimplement near-identical AI-card glue (`BuildAiCard`, `RefreshAvailability`, `OnAiGeneratePressed`, `OnAiDraftComplete`, `SetAiBusy`, `ShowAiStatus`); the duplication already drifted (the `ShowAiStatus` color bug fixed in this pass). It should be a shared `AiDraftCard` control. Also: the intent names "stats, name, lore" but no unit/ability/hero/faction definition schema has a lore/description field, so no draft produces lore (the only schema-consistent reading). — Evidence: Six methods duplicated across `UnitCardPanel`/`AbilityEditorPanel`/`FactionDefinerPanel` with confirmed drift (P1 in this review). `UnitDefinition`/`AbilityDefinition`/`FactionDefinition` expose only `DisplayName`, no lore/flavor field (confirmed by Intent Alignment auditor). Closure = factor a shared `AiDraftCard`; and if lore is genuinely wanted, add a `description`/`lore` field to the definition schemas in a future story. (Blind Hunter + Intent Alignment, Story 8.4 review.)
status: open

### DW-380: The `UnitDefinitionValidator` `[0, 32768)` stat gate admits `0` for stats where zero is degenerate (e.g
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-5-ai-balance-analysis-of-a-faction-scenario-with-editable-suggestions.md`
location: UnitDefinitionValidator.cs:319-325
reason: The `UnitDefinitionValidator` `[0, 32768)` stat gate admits `0` for stats where zero is degenerate (e.g. `attack_speed=0`, `mesh_scale=0`, `collision_radius=0`), so a balance-apply (or any hand-authored edit) can commit a unit that divides-by-zero or renders invisible downstream. Story 8.5 makes reaching these one-click, but the permissiveness is pre-existing and affects all authoring. — Evidence: `CheckStat` (UnitDefinitionValidator.cs:319-325) accepts `[0, Range)` including 0; hand-authored units hit the same gate. Closure = decide per-stat strictly-positive lower bounds (like `projectile_speed`'s rule-2) where zero is degenerate, in the validator, so both manual and AI edits are gated. (Blind Hunter, Story 8.5 review.)
status: open

### DW-381: The per-kind in-flight cancellation apparatus (`_balanceCts` and its 8.4 siblings…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-5-ai-balance-analysis-of-a-faction-scenario-with-editable-suggestions.md`
location: LLMService.cs
reason: The per-kind in-flight cancellation apparatus (`_balanceCts` and its 8.4 siblings `_unitCts`/`_abilityCts`/`_heroCts`/`_factionCts`) is cancelled on a new request but a provider that returns normally just after cancel still enqueues its callback, so a superseded balance/draft run can repaint rows/forms over a newer one; the CTS objects are also never disposed. Pre-existing draft-flow pattern, now duplicated in the balance flow. — Evidence: `RunDraftAsync` only swallows `OperationCanceledException` (LLMService.cs); a normal return past the cancel point still enqueues `onComplete`. Mirrors the 8.4 review's deferred "per-kind cancellation apparatus is dead/unwired" item. Closure = check the token before enqueue (and dispose the superseded CTS), once across all draft/analysis flows. (Blind Hunter, Story 8.5 review.)
status: open

### DW-382: The balance-analysis tunable-field vocabulary (`BalanceSuggestionApplier.TunableFields`) omits movement `speed`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-8-5-ai-balance-analysis-of-a-faction-scenario-with-editable-suggestions.md`
reason: The balance-analysis tunable-field vocabulary (`BalanceSuggestionApplier.TunableFields`) omits movement `speed` (arguably the highest-leverage balance stat) and `xp_bounty`, while including cosmetic `mesh_scale`; the prompt hard-restricts the model to the set and `ValidateBalanceReport` rejects anything outside it, so a Commander cannot get a speed suggestion at all. — Evidence: `speed` was excluded because it quantizes at the `EntityWorld.Create` ctor, outside this story's `ApplyUnitDefinition`-reuse quantize contract; `xp_bounty` is nullable/derived. Closure = a product decision on whether to add `speed`/`xp_bounty` (handling `speed`'s distinct quantize path and `xp_bounty`'s derived-when-null semantics) and whether to drop `mesh_scale`. (Blind Hunter, Story 8.5 review.)
status: open

### DW-383: ScenarioDirector `defeat` action resolves the winner as `OnVictory(1 - a.Faction)`, a 1v1-only "other faction…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-2-expand-the-faction-player-model-to-8-and-audit-every-int-faction-site.md`
location: ScenarioDirector.cs
reason: ScenarioDirector `defeat` action resolves the winner as `OnVictory(1 - a.Faction)`, a 1v1-only "other faction wins" computation that yields a garbage negative faction slot for a.Faction >= 2; N-player FFA winner-on-single-defeat semantics belongs to Story 9.14 (teams/victory). — Evidence: ScenarioDirector.cs ~:2089; reachable only once 8-player `defeat` triggers exist (this story enables them). Not folded into SimChecksum (presentation callback), so no desync — but a real latent presentation bug at slots 3-8.
status: open

### DW-384: TriggerEditorPanel's live custom-event raiser-slot validation still passes `(int)Faction.Player4` while the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-2-expand-the-faction-player-model-to-8-and-audit-every-int-faction-site.md`
location: TriggerEditorPanel.cs:1366
reason: TriggerEditorPanel's live custom-event raiser-slot validation still passes `(int)Faction.Player4` while the load-time validator now uses PLAYER_COUNT, so the editor rejects raiser slots 4-7 the engine accepts (presentation/authoring cap, Story 9.5 / post-1.0 UI-to-8). — Evidence: src/CreationSuite/TriggerEditorPanel.cs:1366. Deliberately fenced as presentation in the 9.2 spec (ship-4-UI-for-1.0); the editor being stricter than load-time is safe but inconsistent.
status: open

### DW-385: MatchChatOverlay maps only Player1-Player4 to names/colors; Player5-8 fall through to a placeholder label and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-2-expand-the-faction-player-model-to-8-and-audit-every-int-faction-site.md`
location: MatchChatOverlay.cs
reason: MatchChatOverlay maps only Player1-Player4 to names/colors; Player5-8 fall through to a placeholder label and default color in an 8-faction match (presentation, Story 9.5). — Evidence: src/UI/MatchChatOverlay.cs ~:46,305. User-visible only when >4 factions play; presentation-layer, out of the 9.2 sim scope.
status: open

### DW-386: BuildingSystem/ResearchSystem `_factions` arrays are now sized 9 but their ctors still populate only…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-2-expand-the-faction-player-model-to-8-and-audit-every-int-faction-site.md`
location: BuildingSystem.cs:82-83
reason: BuildingSystem/ResearchSystem `_factions` arrays are now sized 9 but their ctors still populate only Player1/Player2, so a Player5-8 building/researcher resolves a null faction def (no production/research options) until lobby slot-assignment wires per-slot defs; runtime SetFactionDef already supports arbitrary in-range slots. — Evidence: src/Economy/BuildingSystem.cs:82-83, ResearchSystem.cs:62-63. Full per-slot faction-def population is Story 9.7 (Nakama matchmaking / server-side slot assignment) territory.
status: open

### DW-387: The N=3/N=8 determinism tests are two-run in-process only (no committed cross-process golden), so a cross-platform…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-2-expand-the-faction-player-model-to-8-and-audit-every-int-faction-site.md`
location: MultiFactionExpansionTests.cs
reason: The N=3/N=8 determinism tests are two-run in-process only (no committed cross-process golden), so a cross-platform divergence in the newly-active slots 5-8 code paths would not be pinned; AC3 asked only for two-run equality, and slots 5-8 share the slots-1-4 fold paths already cross-process-pinned by the N=2/N=4 goldens. — Evidence: MultiFactionExpansionTests.cs; the committed goldens (golden-scenario N=2, golden-multifaction N=4) pin cross-process only up to slot 4. Optional hardening: a committed N=8 golden via the WSL cross-platform gate (mind the CRLF-normalization tripwire).
status: open

### DW-388: The map-authoring UI still caps player count at 4 (New-Map picker offers only "2/3/4"; start-position…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-2-expand-the-faction-player-model-to-8-and-audit-every-int-faction-site.md`
location: MapPropertiesPanel.cs
reason: The map-authoring UI still caps player count at 4 (New-Map picker offers only "2/3/4"; start-position markers/colors and the placement ceiling stop at slot 4), so an 8-slot scenario that now passes ScenarioValidator/MapWriteGate cannot be authored or fully visualized in-editor — the validator moved to 8 while the authoring surface stayed at 4 (presentation/authoring, Story 9.5 / post-1.0 UI-to-8). — Evidence: src/CreationSuite/MapPropertiesPanel.cs (PlayerOptions = {2,3,4}); src/UI/StartPositionBridge.cs (MAX_SLOTS = 4, SetPosition/EnsureVisible silently return for slot >= 4); src/UI/EntityPlacer.cs (START_SLOT_CEILING = 4). EntityPlacer.START_SLOT_CEILING is named in the 9.2 spec's Design-Notes scope fences; MapPropertiesPanel/StartPositionBridge are the same ship-4-UI-for-1.0 class but were not yet enumerated. Bounds-guarded (no crash) — slots 4-7 markers just no-op. Closure = raise all three to FactionRegistry.PLAYER_COUNT together in the UI-to-8 story.
status: open

### DW-389: On a mid-match player disconnect the merged-tick server neither shrinks `MergedTickBuilder.Expected` nor…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-3-server-authoritative-merged-tick-rewrite-build-client-gate-spectator-chat-n-2-fr-39-golden-gate.md`
reason: On a mid-match player disconnect the merged-tick server neither shrinks `MergedTickBuilder.Expected` nor force-emits an empty sub-bundle for the departed slot, so the per-tick fan-in gate can never complete again and every surviving player + spectator stalls forever with no terminal HALT — the deterministic freeze-and-continue drop policy is Story 9.6. — Evidence: MergedTickBuilder.TryBuild requires all `Expected` slots to have arrived; DedicatedServer.HandleDisconnect notifies survivors but leaves `_builder.Expected` fixed and `_state` no longer InGame. Pre-rewrite relay also stalled on a missing stream, so this is preserved behavior, not a new defect. The epic sequences disconnect freeze-and-continue as Story 9.6 (9.3 → 9.4 → 9.6). Closure = 9.6's tick-counted empty-command injection + ACK-gated slot handling.
status: done 2026-07-30
resolution: already resolved: DedicatedServer.cs:277-331 HandleDisconnect dictates drop via _dropController.NotifyDrop, ACK-gated commit + PumpFrozenInjection empty-command injection (Story 9.6 freeze-and-continue landed)

### DW-390: The client-side merged-arrival ring/gate in `LockstepManager` (HandleMergedTick keying, the Flush stall/apply…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-3-server-authoritative-merged-tick-rewrite-build-client-gate-spectator-chat-n-2-fr-39-golden-gate.md`
location: LoopbackDesyncSelfTest.cs
reason: The client-side merged-arrival ring/gate in `LockstepManager` (HandleMergedTick keying, the Flush stall/apply predicate, and the bootstrap/delay-gap seeding) is exercised by no automated test — neither Tier-1 (LockstepManager is Godot-coupled, never constructed in the suite) nor the loopback self-test (which drives only Ready + Checksum packets, never TickCommands through the merged ring). — Evidence: LoopbackDesyncSelfTest.cs sends only MakeReady/WriteChecksum (grep-confirmed); every `player.Flush` in the test suite is `ReplayPlayer.Flush`, not `LockstepManager.Flush`. Residual risk is low/fail-loud: a ring-keying regression manifests as a STALL (a wrong-tick occupant is demoted to stall, not mis-applied; max delay 12 < ring 16 on a reliable-ordered channel), and the determinism math (MergedTickApplier) is Tier-1-tested. Closure = extract the ring/gate/seed decision into a Godot-free helper (the builder/applier extraction pattern this story already used) + Tier-1 tests for stall-on-missing, apply-on-arrival, ring-wraparound, and empty-seed no-op.
status: open

### DW-391: `MergedTickApplier.Apply` allocates three fresh scratch arrays (`Faction[8]`, `int[8]`, `UnitOrder[8*32]`) on…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-3-server-authoritative-merged-tick-rewrite-build-client-gate-spectator-chat-n-2-fr-39-golden-gate.md`
location: MergedTickApplier.cs:43-45
reason: `MergedTickApplier.Apply` allocates three fresh scratch arrays (`Faction[8]`, `int[8]`, `UnitOrder[8*32]`) on every call — once per tick per client and per spectator at 30 tps — contradicting the builder's deliberate zero-alloc discipline and adding steady gen-0 GC pressure to the determinism-critical apply path (a GC hitch is exactly what stalls a lockstep tick). — Evidence: MergedTickApplier.cs:43-45 (caller-owned scratch is `new`'d inside Apply, not pooled). ~4KB/tick is modest for gen-0 but the builder pre-allocated all scratch precisely to avoid this; the comment even calls it "caller-owned" yet allocates internally. Closure = hoist the scratch to a caller-owned/pooled buffer (an overload taking the three arrays), matching MergedTickBuilder's preallocation. Not urgent (single apply per tick, small arrays) and cannot desync (deterministic) — a perf/consistency hardening.
status: open

### DW-392: The server's hard-reject arm for a client-sent `PacketType.TickCommandsMerged` logs via `GD.PrintErr` on every…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-3-server-authoritative-merged-tick-rewrite-build-client-gate-spectator-chat-n-2-fr-39-golden-gate.md`
reason: The server's hard-reject arm for a client-sent `PacketType.TickCommandsMerged` logs via `GD.PrintErr` on every occurrence with no rate-limit or disconnect-on-repeat, so a malicious/buggy client can spam merged-shaped packets and flood the server log (a soft log-write DoS) while the work is also doubled by the builder re-rejecting the same packet. — Evidence: DedicatedServer.HandlePacket merged-reject case (unthrottled GD.PrintErr per packet). Low severity on a reliable/authenticated ENet channel, but a server-authoritative posture should bound attacker-triggerable log writes. Closure = rate-limit the log and/or disconnect a peer after N protocol violations (a general per-peer misbehavior counter that also covers the faction-spoof and over-count drop paths).
status: open

### DW-393: A client-triggered drop of an intermediate tick's bundle (faction spoof / over-count / malformed →…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-3-server-authoritative-merged-tick-rewrite-build-client-gate-spectator-chat-n-2-fr-39-golden-gate.md`
reason: A client-triggered drop of an intermediate tick's bundle (faction spoof / over-count / malformed → `MergedTickBuilder.Submit` returns false) makes that tick's fan-in never complete, so under input delay the server can already have emitted later ticks (non-contiguous emission) while the incomplete tick never emits — a permanent, no-HALT freeze for every surviving player and spectator, triggerable by a single misbehaving/malicious client (a griefing/DoS vector). — Evidence: MergedTickBuilder.TryBuild only emits a tick once all `Expected` slots have arrived and advances `_resolvedThrough` to it, with no contiguous-emission guard; a dropped intermediate submit is never resupplied (a client sends each tick once). Distinct trigger from the already-deferred *disconnect* freeze (that is a departed slot; this is a present slot whose bundle was legitimately dropped). Fan-in drops are also swallowed with no `else`-log, so the freeze is undiagnosable server-side. Closure = fold into Story 9.6's freeze-and-continue policy (force-emit the incomplete tick with an empty sub-bundle for the offending slot, or emit a terminal HALT so the freeze is observable) and rate-limit-log the drop.
status: open

### DW-394: The server-side `DedicatedServer` delegation to the tested cores is exercised by no automated test — the fan-in…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-3-server-authoritative-merged-tick-rewrite-build-client-gate-spectator-chat-n-2-fr-39-golden-gate.md`
reason: The server-side `DedicatedServer` delegation to the tested cores is exercised by no automated test — the fan-in wiring (`FanInTickCommands`: slot/len into `_builder.Submit`, `TryBuild`, then `BroadcastCommands`), the `Chat` re-stamp branch (decode → `ServerLobbyPolicy.StampChatFaction` → `MakeChat` re-encode → rebroadcast), and the client-sent-`TickCommandsMerged` hard-reject dispatch — so an adapter-level transposition (wrong slot/len, omitted broadcast, dropped chat re-encode letting a spoofed faction byte through) would ship green while every underlying unit primitive passes in isolation. — Evidence: DedicatedServer is Godot-coupled and constructed only in src/ + LoopbackDesyncSelfTest (which routes no command/chat packet through the merged path); MergedTickBuilder/ServerLobbyPolicy/MergedTickPacket are Tier-1-tested individually but their composition in HandlePacket/FanInTickCommands is not. Server-node sibling of the already-deferred client-ring coverage gap; the intent accepts Godot-coupled nodes as untestable-at-Tier-1 by design, so residual risk is low (thin wiring, twice-reviewed). Closure = extract the server delegation seams into Godot-free helpers (the builder/applier/lobby-policy extraction pattern this story already used) or add a loopback path that drives a command + a chat packet end-to-end through the merged fan-in.
status: open

### DW-395: The dedicated server's Pong handler folds any Pong into the per-slot RTT EWMA without checking the ping seq…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: The dedicated server's Pong handler folds any Pong into the per-slot RTT EWMA without checking the ping seq, unlike the client's seq-guarded HandlePong. — Evidence: A stale/duplicate Pong within the 10s sanity window is accepted as a fresh sample = serverNow - oldSenderMs, inflating the per-slot RTT and over-dictating delay (bounded by the [2,12] clamp); asymmetric with the client's LockstepManager.HandlePong seq check.
status: open

### DW-396: Server RTT clock-width mismatch (uint ping timestamp vs ulong subtraction) silently freezes delay adaptation after…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: Server RTT clock-width mismatch (uint ping timestamp vs ulong subtraction) silently freezes delay adaptation after ~49.7-day server uptime. — Evidence: Ping stamps (uint)Time.GetTicksMsec() (wraps at 2^32 ms) but the Pong RTT subtracts against the full-width ulong clock; past ~49.7 days every sample reads ~4.3e6 ms and is rejected by the >10000ms filter, so the server-dictated delay stays frozen at INPUT_DELAY with no error.
status: open

### DW-397: The start-state agreement gate and DelayController ACK/RTT indexing assume ready players occupy dense slots…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: The start-state agreement gate and DelayController ACK/RTT indexing assume ready players occupy dense slots [0,expected); a non-contiguous layout false-HALTs or wedges delay-commit. — Evidence: ServerLobbyPolicy.CheckStartStateAgreement reads perSlotHash[0..expected) and DelayController indexes by slot<Expected; a ready player at slot>=expected reads a default-0 hash (false StartStateDisagreement) and drops its ACK (AllAcked never true). Only reachable once MAX_PLAYERS>2 (Story 9.7/9.15), which the 9.4 Never-list excludes — flagged as latent for that work.
status: open

### DW-398: DedicatedServer does not reset _readyHash/_readyVersion on the start-state agreement-fail branch, leaving stale…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: DedicatedServer does not reset _readyHash/_readyVersion on the start-state agreement-fail branch, leaving stale per-slot agreement data across a subsequent Ready. — Evidence: On agreement failure the server broadcasts HALT and sets _state=BothReady without clearing the per-slot hash/version; benign today (HALT is terminal for clients, no retry path) but a latent trap once reconnect/re-ready (9.6) exists.
status: open

### DW-399: No end-to-end integration test drives a server DelayDirective through the client CommitDelayChange gap-seed and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: No end-to-end integration test drives a server DelayDirective through the client CommitDelayChange gap-seed and MergedTickBuilder across two clients to prove they stay in lockstep across a delay change. — Evidence: The pipelining-desync class is Tier-1-covered at the DelayController maturity gate, but the Godot-coupled node wiring (LockstepManager directive receipt + gap pre-seed vs the builder's no-emit-on-gap) is exercised only by the non-Tier-1 loopback smoke, and that smoke does not vary the delay mid-match; same accepted boundary as Story 9.3's deferred client-ring gap.
status: open

### DW-400: The DelayController directive/all-N-ACK state machine has no ACK timeout or recovery; a lost ACK or a player…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: The DelayController directive/all-N-ACK state machine has no ACK timeout or recovery; a lost ACK or a player dropping while a directive is pending leaves _pending stuck forever, silently disabling adaptive delay for the rest of the match. — Evidence: _pending only clears via Commit, which needs AllAcked over the fixed Expected slot set. Reliable transport means a lost ACK only occurs on disconnect (Story 9.6 domain, excluded by the 9.4 Never-list), but the fail-silent stall — no re-send, no timeout, no Expected re-count — is worth an explicit recovery pass when 9.6 lands.
status: open

### DW-401: DedicatedServer's delay-frontier fields (_latestSeenTick, _sincePing, _pingSeq) are not reset when…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: DedicatedServer's delay-frontier fields (_latestSeenTick, _sincePing, _pingSeq) are not reset when _delayController is rebuilt at match start, so a second match in one process computes applyAtTick from a stale (large) frontier and the first adaptive-delay directive schedules at an unreachable tick. — Evidence: _delayController is reconstructed on entry to InGame but the sibling instance fields are never zeroed alongside it. No live path today (a match is one-shot per server process), so latent — but it silently breaks adaptive delay the moment match-restart/reconnect (9.6) is added. Reset them where _delayController is constructed, or pin the one-shot assumption.
status: open

### DW-402: The client-side PROTOCOL_VERSION gates (LobbyUi inbound-Hello block and the peer-Ready version block) have no…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: The client-side PROTOCOL_VERSION gates (LobbyUi inbound-Hello block and the peer-Ready version block) have no Tier-1 coverage, unlike the server-side ServerLobbyPolicy.CheckStartStateAgreement version gate which is unit-tested — an asymmetric gap over the advertised D3.8 client-side closure. — Evidence: Story94WireTests only proves the codec reads the version back; no test exercises LobbyUi's decision to block on mismatch. Deleting either client-side version clause reopens the D3.8 gap (a v1 client proceeding against a v2 peer) with a green suite. LobbyUi is Godot-coupled/not instantiable in Tier-1 — an extract-for-testability candidate mirroring DelayMath.ResolveDirectiveReceipt, same accepted boundary as the client directive-receipt gap.
status: open

### DW-403: On a client-side protocol-version mismatch, LobbyUi hides the Ready button and clears confirmation with no…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: On a client-side protocol-version mismatch, LobbyUi hides the Ready button and clears confirmation with no transport teardown and no recovery, so a later valid Hello on the same connection cannot restore the lobby to a ready-able state. — Evidence: The Hello/Ready mismatch branches set a status string and _readyBtn.Visible=false / _peerReadyConfirmed=false then break/return, with no state that re-enables the button. Fail-closed blocking (the intent's requirement) is satisfied, so low severity; the permanent UI lockout is a robustness nit for the reconnect/retry (9.6) path.
status: open

### DW-404: LobbyUi forces server-dictated delay mode on ANY online topology (ServerDictated = _assignedFaction != Neutral ||…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-4-server-dictated-adaptive-input-delay-start-state-agreement-protocol-version-rulesethash-gates.md`
reason: LobbyUi forces server-dictated delay mode on ANY online topology (ServerDictated = _assignedFaction != Neutral || _onlineModeActive), which disables the client's own ping/propose loop; an online path with no server DelayController then never adapts the delay, silently pinning it at INPUT_DELAY for the whole match. — Evidence: FireMatchStart sets ServerDictated true whenever _onlineModeActive, and LockstepManager.Flush/MaybeProposeDelayChange hard-gate SendPing + self-proposal off in that mode — but the sole DelayController (which issues DelayDirective) lives in DedicatedServer. A non-dedicated online/Nakama relay would leave the client a delay follower with nothing dictating, so no DelayDirective ever arrives and the delay never adapts (graceful degradation, not a desync). Newly introduced by this story's ServerDictated gating; reachability depends on an online topology the diff does not establish. Closure = gate ServerDictated on the presence of an actual server delay authority (not merely "online"), or confirm every online path stands up a DelayController, and add a test/assert for the Nakama path.
status: open

### DW-405: Worker-build placement (MainScene QueueWorkerBuild) is a direct sim mutation not routed through the lockstep…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-5-local-faction-parameterization-remove-every-player1-hardcode-from-the-presentation-layer.md`
location: MainScene.cs:562
reason: Worker-build placement (MainScene QueueWorkerBuild) is a direct sim mutation not routed through the lockstep EnqueueOrder seam, so an online client's building placement applies locally-only and is never server-replicated → a reachable-online SimChecksum desync. — Evidence: MainScene.cs:562 calls _buildSys.QueueWorkerBuild(... faction ...) directly (spends resources + sets world.CommandState = Build, folded into SimChecksum), unlike every other order site which defers via _lockstep?.EnqueueOrder(...) ?? true and lets the server re-stamp. Pre-existing: Story 9.5 only changed the faction argument (Player1 → local faction); the not-replicated-online property is unchanged and total (any online build already desyncs regardless of faction). The 9.5 intent explicitly excludes build-command online replication as orthogonal. Closure = route worker-build through EnqueueOrder like Train/Rally (Story 9.7/9.15 online-build work), or keep it documented offline-only.
status: open

### DW-406: Spectator minimap fog is not revealed — the spectator branch sets FogBridge.RevealAll for the 3D overlay but…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-5-local-faction-parameterization-remove-every-player1-hardcode-from-the-presentation-layer.md`
reason: Spectator minimap fog is not revealed — the spectator branch sets FogBridge.RevealAll for the 3D overlay but MinimapBridge reads FogOfWarSystem.Grid directly and ignores RevealAll, so a spectator's minimap stays fogged to the (default Player1) viewer while the main view is fully open. — Evidence: MatchLifecycleController.OnMatchStart spectator branch sets _ctx.FogBridge.RevealAll = true but never retargets the fog system; MinimapBridge.DrawDots/fog texture read _fog.Grid with no RevealAll honoring. Pre-existing (MinimapBridge always read Grid directly; RevealAll was only ever a FogBridge/GPU-overlay concern); surfaced by Story 9.5's spectator-perspective analysis, not caused by it. Closure = honor RevealAll in the minimap fog read (or drive a fully-revealed grid for spectators) so the two views agree.
status: open

### DW-407: The pre-existing raw LockstepManager.LocalFaction consumers (personalised DSL-readback overlays and hero-owner…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-5-local-faction-parameterization-remove-every-player1-hardcode-from-the-presentation-layer.md`
location: CustomHudOverlayPhase.cs:43
reason: The pre-existing raw LockstepManager.LocalFaction consumers (personalised DSL-readback overlays and hero-owner minting) are not clamped through EffectiveLocalFaction, so after an online/spectate match a same-process offline F5 resolves them to the stale online faction — a two-tier source of truth the 9.5 clamp closed only for its own sites. — Evidence: Story 9.5 routed its interaction/perspective sites through the new LockstepManager.EffectiveLocalFaction (offline/spectator → Player1) and added GoOffline() at the return-to-Edit seam, but GoOffline resets IsOnline/IsSpectator and NOT LocalFaction. Sites still reading raw LocalFaction — CustomHudOverlayPhase.cs:43 / ObjectiveLogOverlayPhase.cs:41 / TriggerDebugOverlayPhase.cs:41 (localFactionGetter → PlayerSlotForFaction personalisation) and HeroPickerPhase.cs:56 / MainScene.cs:501,824,1845,1851 (ownerSlot hero minting) — therefore see the stale Player2/Neutral from the prior online match in a subsequent offline playtest. Pre-existing (these consumers predate 9.5 and were named by the intent as already-consuming LocalFaction, hence out of 9.5's scope); surfaced by the follow-up intent-alignment analysis. Closure = route these raw reads through EffectiveLocalFaction (or reset LocalFaction in GoOffline) so the whole presentation layer shares one clamped source of truth, and confirm which of these consumers are actually offline-reachable.
status: open

### DW-408: MinimapBridge.DrawDots draws every unit and building dot with no fog-visibility gate, so each client sees all…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-5-local-faction-parameterization-remove-every-player1-hardcode-from-the-presentation-layer.md`
reason: MinimapBridge.DrawDots draws every unit and building dot with no fog-visibility gate, so each client sees all enemy positions on its minimap regardless of fog — a competitive-fairness gap now that non-Player1 clients view their own perspective. — Evidence: DrawDots loops _world.HighWaterMark / _buildings and paints each alive entity's dot (col = FactionOf == me ? P1_COLOR : P2_COLOR) with no _fog.IsVisible check. Pre-existing (the minimap never gated dots on fog); distinct from the already-deferred spectator-RevealAll entry — that one is fog-not-REVEALED for spectators, this one is fog-not-CONCEALED for players. Surfaced by Story 9.5's per-client perspective work, which makes a hidden-information leak on the minimap matter for real multiplayer. Closure = gate enemy dots on FogOfWarSystem.IsVisible (mirroring the fog overlay) before drawing, so the minimap respects the same vision the main view does.
status: open

### DW-409: The DropController freeze state machine deadlocks at N>=3 — a second disconnect while a drop directive is pending…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: The DropController freeze state machine deadlocks at N>=3 — a second disconnect while a drop directive is pending is swallowed by the one-directive-at-a-time guard (NotifyDrop returns false, the second slot is never frozen), and a survivor that disconnects before ACKing is never pruned from the pending _isSurvivor snapshot, so AllAcked() can never complete. — Evidence: NotifyDrop's `if (_pending) return false` drops the concurrent drop, and _isSurvivor is a frozen snapshot captured at NotifyDrop with no reconciliation against later disconnects; either path leaves the merge fan-in (Expected=N) waiting forever on an un-frozen slot → all survivors stall permanently, no MATCH SUMMARY. Unreachable at the shipped MAX_PLAYERS=2 (a lone survivor vanishing → survivors==0 → the clean match-over branch), but exercised by DropControllerTests at N=3. Caused by this story's N-shaped design; the epic sequences live N>2 enablement/verification to Story 9.7 (Nakama N-player) / 9.15 (four-player verified e2e), which own this fix. Closure = queue-or-escalate a concurrent drop and add DropController.RemoveSurvivor(slot) reconciling _isSurvivor/_acked + re-checking AllAcked on any in-match disconnect.
status: open

### DW-410: The drop directive/ACK state machine has no ACK timeout or liveness fallback — a surviving player that is…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: The drop directive/ACK state machine has no ACK timeout or liveness fallback — a surviving player that is transport-connected but hung (never sends DropAck) leaves the freeze forever uncommitted, so FrozenSlotInjector never runs and every other survivor + spectator stalls indefinitely. — Evidence: Commit fires only on AllAcked() over the survivor set; there is no deadline, re-send, or force-commit. At N=2 the blast radius is spectators (the lone survivor is the only ACKer; if it hangs the match is effectively dead anyway), widening at N>=3. This is the disconnect-domain continuation of the 9.4-deferred "no ACK timeout" entry. Closure = a tick-bounded escalation (force-commit over ACKed survivors, or abort to MATCH SUMMARY) consistent with the story's tick-counted stance; sequence with the N-player robustness work (9.7/9.15).
status: open

### DW-411: The DedicatedServer freeze adapter (FactionToSlot mapping, survivors>0 vs survivors<=0 gating, applyAtTick…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: The DedicatedServer freeze adapter (FactionToSlot mapping, survivors>0 vs survivors<=0 gating, applyAtTick capture, DropAck->commit->DropReporter->inject chain) has no xUnit coverage — only the DEBUG-only LoopbackDesyncSelfTest smoke exercises it, and only the survivors==1 happy path; the survivors<=0 "match truly over" branch is unverified anywhere. — Evidence: No *DedicatedServer*Tests exists; the decision logic it wires (DropController/FrozenSlotInjector/collector) is Tier-1-tested but the Godot-coupled node glue is not, mirroring the accepted Story 9.3/9.4 boundary (Godot-coupled node wiring exercised only by the non-Tier-1 loopback smoke). Closure = extract the disconnect/ACK decision glue into a Godot-free helper unit-tested like DropController, or run the loopback smoke in CI and add a survivor-less-drop case.
status: open

### DW-412: FactionToSlot returns the first slot whose SLOT_FACTION matches with no injectivity guard; a duplicate faction in…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: FactionToSlot returns the first slot whose SLOT_FACTION matches with no injectivity guard; a duplicate faction in SLOT_FACTION would resolve a DropAck to the wrong slot and silently no-op the ACK (stall), and no test covers a drop where the dropped slot led the merge frontier (the common "drop the lagging/racing player" case). — Evidence: SLOT_FACTION = FactionRegistry.ToFaction(i) is injective by construction today (distinct faction per slot), so the mis-map is unreachable — but there is no assert pinning it, and RecordAck's droppedSlot==_pendingDroppedSlot check turns a mis-resolution into a silent lost ACK rather than a logged error. The drain's use of the global _latestSeenTick frontier (which includes the dropped peer's pre-drop lead) is correct only via MergedTickBuilder's ACCEPT_WINDOW reject; no test constructs "dropped slot was ahead of the survivor." Closure = assert SLOT_FACTION injectivity at BuildSlotFactions, and add a dropped-slot-led-frontier scenario.
status: open

### DW-413: AC3's named passive-sim examples (a unit mid-health-regen, a projectile in flight straddling the drop) are not…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: AC3's named passive-sim examples (a unit mid-health-regen, a projectile in flight straddling the drop) are not specifically constructed by the golden mid-match-drop scenario, which uses a Move-only order stream + the order-sensitive bump fold as the "passive sim continues bit-identically" proxy. — Evidence: MidMatchDropScenario runs the REAL SimulationHost pipeline (MovementSystem/CombatSystem/ProjectileSystem all tick via StepOnce), so movement-in-progress persistence IS exercised and the general "passive sim continues" property holds; but no attack/regen/projectile is configured (default FactionDefinition, Move + bump only), so the specific AC3 straddle cases are covered only transitively by determinism. Closure = add a projectile-in-flight and/or regen element to the drop scenario so the AC3 examples are exercised at the surface they name.
status: open

### DW-414: After a 1v1 drop the checksum quorum floors to a single reporter (its own trivial majority), so ServerHost keeps…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: After a 1v1 drop the checksum quorum floors to a single reporter (its own trivial majority), so ServerHost keeps emitting PASS windows for the rest of the match; those windows are pure liveness/observability, yet a human reading the MATCH SUMMARY cannot distinguish them from genuine cross-peer attestation — post-drop "PASS" over-claims determinism enforcement. — Evidence: DropExpectedReporter floors _expected to 1 and ProcessVerdict then rubber-stamps every window as Passing (a lone reporter is always its own majority). The code carries honest doc-comments to this effect (added in the review_loop_iteration 0 pass) but the observable verdict/summary surface still reads PASS with no INCONCLUSIVE/attestation-suspended marker. Caused by this story's quorum-reduction path; at MAX_PLAYERS=2 every drop reaches floor-1, so this is always the state after any 1v1 disconnect. Distinct from the prior doc-honesty patch (comments only) — this is a verdict-reporting gap on the human-facing surface. Closure = surface post-drop floor-1 windows as INCONCLUSIVE / "attestation suspended (single reporter)" in the verdict + MATCH SUMMARY rather than PASS.
status: open

### DW-415: The disconnect-driven checksum re-tally is only ever tested through to a clean lone-survivor PASS — the branch…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: The disconnect-driven checksum re-tally is only ever tested through to a clean lone-survivor PASS — the branch where DropExpectedReporter re-tallies a still-in-flight bucket to a DESYNC (no strict majority → HALT) or majority+minority (DesyncAlert) verdict, then routed via ServerHost.DropReporter → ProcessVerdict's HALT/alert branch, is exercised by no test. — Evidence: ServerChecksumCollectorDropTests all assert HasMajority==true / empty Minority; ServerHostTests.DropReporter_KeepsCompletingWindows asserts Passing / !Halted only. No drop-path test reaches ProcessVerdict's HALT branch, and none asserts DropReporter's `if (Halted) break` stops routing later windows. Reachable only at N>=3 (at N=2 the post-drop quorum is a single reporter that can never form a divergent majority), so it is latent behind the same MAX_PLAYERS=2 gate as the N-player enablement work (Stories 9.7/9.15) that will make a post-drop desync verdict meaningful. Closure = when N>=3 ships, add a collector test (two remaining reporters disagreeing on a re-tallied tick → HasMajority==false) and a ServerHost test asserting the drop path drives Halted/DesyncAlert and honours the `if (Halted) break`.
status: open

### DW-416: The mid-match-drop golden gate is entirely relative (two-run determinism, divergence from a no-op-injector…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: The mid-match-drop golden gate is entirely relative (two-run determinism, divergence from a no-op-injector reference, divergence from a no-drop control) with no committed golden tail and no positive "idle-equivalence" baseline, so it pins that the freeze is deterministic-and-different but not that it is the CORRECT idle-but-folded state — a deterministic-but-wrong injector output that still differs from both references would pass, and the test comment's claim that a "faction dropped from the sim" regression is caught is unfounded (the harness has no faction-removal path to diverge from). — Evidence: MidMatchDropDesyncTests asserts only (a) two-run equality, (b) divergence-from-no-inject at tick>=dropTick, (c) divergence-from-no-drop at tick>=dropTick, (d) pre-drop equality; MidMatchDropScenario is deliberately baseline-free (no committed golden hash). "Wrong faction stamped" is backstopped at unit level by FrozenSlotInjectorTests, but "correct idle vs any other deterministic state" is not pinned at the scenario surface. Caused by this story's decision to keep the drop scenario baseline-free. Closure = commit a recorded post-drop checksum tail, OR add a positive assertion comparing the drop run byte-for-byte against a control where the dropped player issues explicit hold/idle orders (proving "idle", not merely "different"); and correct the test comment to describe what is actually verified.
status: open

### DW-417: The surviving CLIENT's Flush-gate unstall — the literal center of the story's problem statement ("every surviving…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-6-deterministic-disconnect-freeze-and-continue-drop-policy.md`
reason: The surviving CLIENT's Flush-gate unstall — the literal center of the story's problem statement ("every surviving client's Flush gate stalls permanently") — is verified only transitively: no test drives a real LockstepManager.Flush across a mid-match drop; the golden applies merged packets straight into an EntityWorld via ServerHost/MergedTickApplier (bypassing Flush), and the DEBUG-only LoopbackDesyncSelfTest uses a minimal Peer harness that merely counts received merged packets (MergedCount++), never running the client's stall gate. — Evidence: The client-side "no ring pre-seed needed / Flush fills and unstalls normally" claim (HandleDropDirective doc-comment) is asserted by comment + transitive reasoning ("merged packets arrive ⇒ Flush would fill"), not by a test that stalls a real LockstepManager.Flush and shows it resumes deterministic stepping post-drop. Notably the sibling delay path DID need a local pre-seed, so the "unlike a delay change" asymmetry is exactly the kind of claim a test should pin. Distinct from the already-deferred DedicatedServer-adapter-no-xUnit entry (that is the SERVER glue; this is the CLIENT Flush surface). Closure = add a client-side drop test that stalls LockstepManager.Flush on the dropped slot, injects the server merged stream, and asserts the client resumes stepping without a pre-seed.
status: open

### DW-418: The Godot-coupled DedicatedServer fan-in / AssignedRoster-freeze / SlotAllocation-classify wiring has no automated…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-7-nakama-n-player-matchmaking-parties-api-server-side-slot-assignment-lobby-matchmaking-ui.md`
location: DedicatedServer.cs
reason: The Godot-coupled DedicatedServer fan-in / AssignedRoster-freeze / SlotAllocation-classify wiring has no automated xUnit coverage — only the pure functions are tested; the adapter's call-with-right-arguments is proven only by the manual N=4 loopback smoke. — Evidence: DedicatedServer.cs uses `using Godot;` and is excluded from ProjectChimera.Sim.Tests; the freeze/classify/fan-in call sites (HandleConnect, HandleReady, StartGame roster freeze) run in no `dotnet test` case at any N. Same accepted node-wiring boundary as Stories 9.3/9.4/9.6.
status: open

### DW-419: On the dedicated/online path a player's own lobby-chat line renders twice — LobbyUi.OnChatSubmitted appends the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-7-nakama-n-player-matchmaking-parties-api-server-side-slot-assignment-lobby-matchmaking-ui.md`
reason: On the dedicated/online path a player's own lobby-chat line renders twice — LobbyUi.OnChatSubmitted appends the message optimistically AND the dedicated server re-stamps and BroadcastReliable's it back to the sender (which includes the sender), with no dedupe. — Evidence: LobbyUi.OnChatSubmitted does a local AppendChat, and DedicatedServer.HandlePacket's LobbyChat case (added in review_loop_iteration 0 as the P1 dedicated-path relay) rebroadcasts to every connected peer including the origin; the P2P path has no such rebroadcast so the optimistic echo is correct there — the double-render is specific to the dedicated path and is acknowledged in a code comment but not resolved. Caused by this story's P1 relay patch. Closure = suppress the optimistic local echo when connected to a dedicated server, or tag/dedupe the server's echo of the sender's own message.
status: open

### DW-420: A dedicated-server spectator (transport slot >= ExpectedPlayers) is shown a bogus 2-player "Host confirmed — click…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-7-nakama-n-player-matchmaking-parties-api-server-side-slot-assignment-lobby-matchmaking-ui.md`
reason: A dedicated-server spectator (transport slot >= ExpectedPlayers) is shown a bogus 2-player "Host confirmed — click Ready" P2P-style lobby, because LobbyUi's Hello handler treats every Neutral-faction Hello as a P2P host confirmation and cannot distinguish it from the server's spectator Hello(Neutral). — Evidence: DedicatedServer sends MakeHello(Faction.Neutral) to a slot classified Spectator by SlotAllocation; LobbyUi's `case PacketType.Hello` Neutral branch unconditionally marks slots 0+1 occupied and shows the Ready button. Harmless to the match (the server drops a spectator's Ready via `slot >= ExpectedPlayers`), so it is a UI misrepresentation only, and spectating is documented headroom (MAX_SLOTS spectator slots) rather than a shipped/verified AC — hence deferred, not patched. Caused by this story introducing the dedicated N-player + spectator-classification path. Closure = carry a role/isDedicated discriminator (or a distinct spectator Hello) so a Neutral Hello on the dedicated path renders a spectator view rather than a P2P host-confirmed 2-slot lobby.
status: open

### DW-421: `ContentPackager.RewriteManifest` rewrites the shipped `.chimera.zip` manifest non-atomically…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-8-proof-of-play-token-pre-publish-quality-ip-consent-gate-publish-chimera-zip-to-mod-io.md`
location: ContentPackager.cs
reason: `ContentPackager.RewriteManifest` rewrites the shipped `.chimera.zip` manifest non-atomically (delete-then-write-in-place under `ZipArchiveMode.Update`), so a throw between the `manifest.json` delete and the commit-on-Dispose leaves the creator's own local package with no manifest — permanently unreadable by every future `ReadManifest`/`Unpack`. — Evidence: ContentPackager.cs RewriteManifest does `archive.GetEntry("manifest.json")?.Delete()` then `CreateEntry` + `JsonSerializer.Serialize` + `s.Write`, all inside one `using var archive = ZipFile.Open(..., Update)`; an exception from Serialize/Write (or a failed commit on Dispose) unwinds the `using` and flushes an archive whose only manifest entry has already been deleted. Caused by this story (RewriteManifest was introduced in this spec's iteration-0 consent-persistence patch); distinct from the iteration-0-rejected non-atomic TOKEN write, which is fail-soft on read — a corrupt manifest is not. Low likelihood (small-manifest Serialize/Write rarely throws) and the UI already refuses the upload on the caught exception, but the local zip is left corrupt. Closure = serialize the manifest bytes BEFORE deleting the old entry, or write to a temp copy and atomically replace the original on success.
status: open

### DW-422: No export-side producer populates `ContentPackager.PackOptions.AssetPaths`, so no real editor/publish flow bundles…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-9-content-hash-integrity-verification-on-download-runtime-binary-asset-ingest.md`
location: ContentPackager.cs
reason: No export-side producer populates `ContentPackager.PackOptions.AssetPaths`, so no real editor/publish flow bundles a scenario's referenced custom `.glb` models — the WC3-parity "import → package" asset-reference-resolution slice is not built. — Evidence: Grep shows `AssetPaths` is referenced only inside `ContentPackager.cs` (declaration + consumption); no `WinConditionPhase`/export path passes it. epics.md:3707 frames "custom binary assets flow import → package → runtime ingest" as a distinct Import Manager concern. Story 9.9 correctly provides the Pack API + integrity + runtime-ingest machinery, but until a producer feeds `AssetPaths`, packages carry no bundled assets and the render path has no inputs on real content.
status: open

### DW-423: `ContentPackager.Pack` computes the asset hash by reading each source file, then re-reads the same files at…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-9-content-hash-integrity-verification-on-download-runtime-binary-asset-ingest.md`
reason: `ContentPackager.Pack` computes the asset hash by reading each source file, then re-reads the same files at `WriteEntry` — a TOCTOU window where a source mutated between the two reads yields a package that fails its own `Unpack` integrity check. — Evidence: In `Pack`, `HashFiles` reads the asset bytes for the hash and the later `WriteEntry(archive, ..., File.ReadAllBytes(f))` re-reads them; the two byte snapshots can differ if the file changes in between. Same latent shape now shared by the generalized terrain path. Low-likelihood (single-threaded export of stable on-disk assets), pre-existing pattern, not caused by this story's core.
status: open

### DW-424: `ContentPackager.Pack` validates only duplicate leaf names (P3), not asset extension or size, while `Unpack`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-9-content-hash-integrity-verification-on-download-runtime-binary-asset-ingest.md`
location: ContentPackager.cs
reason: `ContentPackager.Pack` validates only duplicate leaf names (P3), not asset extension or size, while `Unpack` rejects both — so a creator can bundle a non-`.glb` or over-`MaxAssetBytes` file into a package that packs cleanly yet every downloader's `Unpack` rejects, a self-invalidating package discovered only after publish. — Evidence: ContentPackager.cs Pack asset loop (:144-156) folds any existing source file and only guards duplicate `assets/{leaf}` collisions; Unpack (:390-397) throws located InvalidDataException on a disallowed extension or an entry over MaxAssetBytes. The pack/unpack guards are asymmetric. Non-adversarial creator footgun (not a tampered archive). Caused by this story introducing the Pack AssetPaths + Unpack asset-verify seam. Closure = mirror the extension + size checks into Pack (fail fast at export) so a package that would fail its own Unpack cannot be produced.
status: open

### DW-425: A download that fails integrity verification cleans only the throwaway verify cache…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-9-content-hash-integrity-verification-on-download-runtime-binary-asset-ingest.md`
reason: A download that fails integrity verification cleans only the throwaway verify cache (`user://package_cache/<modId>/`), not the raw downloaded `.chimera.zip` in `user://packages/`, so `RefreshLocal` (which scans that directory) re-lists the rejected package as an unverified local card on the next launch. — Evidence: ContentBrowserPanel.OnDownloadComplete (:852-896) marks a bad download "Corrupt ✗", never adds it to `_downloadComplete`, and its `finally` deletes only `cacheDir`; `localPath` under `user://packages/` (the RefreshLocal scan dir, :781) is left in place and is not re-verified when RefreshLocal lists local packages. Caused by this story's download-verify wiring. Closure = on the InvalidData/verify-failed branches, delete the rejected `localPath` (or move it to a quarantined dir), and/or have the local-tab listing re-run integrity verification before offering a package as playable.
status: open

### DW-426: Load-path asset ingest enumerates the extracted `imported_maps/<id>/assets/` directory rather than the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-9-content-hash-integrity-verification-on-download-runtime-binary-asset-ingest.md`
reason: Load-path asset ingest enumerates the extracted `imported_maps/<id>/assets/` directory rather than the integrity-verified manifest `AssetFiles` list, and the import dir is never cleared before extraction, so a stale/orphan `.glb` left from a prior import to the same `manifest.Id` can be ingested and rendered without having passed the current package's integrity check. — Evidence: FactionVisualsPhase.IngestImportedAssets derives `mapId` from the scenario-path stem and enumerates the on-disk assets dir; ContentBrowserPhase.HandleLoadMap extracts to `user://imported_maps/{manifest.Id}/` with `overwrite:true` and never deletes the directory first. Two distinct packages sharing an author-chosen `Id`, or a failed prior extraction, leave orphan files the ingest picks up. Requires same-Id reuse so lower-likelihood, but the rendered mesh would be unverified against the loaded package. Closure = ingest only the manifest's verified AssetFiles logical ids, and clear `imported_maps/<id>/` before extracting a fresh package into it.
status: open

### DW-427: The custom-mesh render resolution (MeshLoader registry overload) matches a unit's `MeshPath` against registered…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-9-content-hash-integrity-verification-on-download-runtime-binary-asset-ingest.md`
reason: The custom-mesh render resolution (MeshLoader registry overload) matches a unit's `MeshPath` against registered logical ids by exact, case-sensitive string; a near-miss (case difference, a `res://` or bare-filename form instead of the `assets/foo.glb` logical id) silently falls through to the box placeholder with no diagnostic, so a mis-authored custom unit renders grey with nothing for the author to debug. — Evidence: The new 4-arg `MeshLoader.LoadFromGlb(..., AssetRegistry?)` overload resolves via an exact `registry.TryGet(resPath)` and otherwise returns the placeholder; there is no log of the miss and no documented MeshPath convention. On case-insensitive Windows FS the on-disk file resolves but the registry key lookup does not, compounding the mismatch. Non-adversarial authoring/debuggability gap. Closure = log the registry miss (listing available ids) when a non-`res://` MeshPath fails to resolve, normalize the key (case-fold/trim), and document the exact MeshPath id convention for content authors.
status: open

### DW-428: Online-tab browse requests are fire-and-forget `Task.Run` calls with no request-generation/sequence token, so…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-10-content-browser-delegating-browse-search-tag-sort-subscribe-rate-to-mod-io.md`
reason: Online-tab browse requests are fire-and-forget `Task.Run` calls with no request-generation/sequence token, so rapid sort-change / tag-toggle / search re-issues can complete out of order and leave the panel rendering a stale mod set while the sort dropdown and tag chips show the newer query. — Evidence: `ModIoService.BrowseModsAsync` launches an unsequenced `Task.Run`; `ContentBrowserPanel.OnBrowseComplete` always calls `PopulateOnlineList` with whatever arrives, in arrival order. Pre-existing async-browse shape (the search field already re-issued this way before Story 9.10), amplified by the new sort dropdown + tag chips that each re-issue `BrowseOnline`. Latency-dependent and self-heals on the next single browse, so low-likelihood in practice. Closure = stamp each browse with an incrementing request id captured in the closure and drop any `OnBrowseComplete` whose id is not the latest.
status: open

### DW-429: Replay recording only fires on the online lockstep `OnMatchStart` player path, so offline skirmish/AI matches…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-11-replay-v2-tagged-body-scenario-re-gate-replay-ux-browser-playback-perspective.md`
reason: Replay recording only fires on the online lockstep `OnMatchStart` player path, so offline skirmish/AI matches produce no `.chmr` file — the new replay browser / save-replay UX is effectively unusable for solo play until recording is extended to offline matches. — Evidence: `MatchLifecycleController.OnMatchStart` calls `StartRecording()` only inside the player (non-spectator) branch, immediately before `_ctx.Lockstep.GoOnline(localFaction)` (line 149-150); there is no offline/skirmish recording trigger. Pre-existing (Story 9.7 scoped recording to the player/non-spectator online path), surfaced by 9.11's browser+save UX which assumes replays exist for matches the solo dev plays. Closure = add a recording trigger on the offline match-start path so skirmish/AI matches also produce a v4 `.chmr`.
status: open

### DW-430: The v4 replay header embeds a `rulesetHash` (and the player exposes `RulesetHash`), but playback re-gates ONLY the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-11-replay-v2-tagged-body-scenario-re-gate-replay-ux-browser-playback-perspective.md`
reason: The v4 replay header embeds a `rulesetHash` (and the player exposes `RulesetHash`), but playback re-gates ONLY the scenario hash — a replay recorded under different ruleset/effect-graph caps than the current build passes the scenario gate and plays, silently desyncing at the first ruleset-sensitive effect, the exact silent-desync class this story eliminates for scenario drift. — Evidence: `MatchLifecycleController.TryLoadReplay` computes only `CanonicalModelHash.Compute` for the loaded scenario and passes it with `player.ScenarioHash` to `ReplayPlayer.ScenarioGateBlockReason` (scenario-only); the embedded `rulesetHash` is read into the header and never compared, whereas the live MP `MatchAgreementHash` mixes BOTH scenario and ruleset hashes. The intent scoped the re-gate to the scenario hash, so this is a latent gap beyond the story's contract rather than a spec deviation. Closure = fold a `RulesetHash.Compute()` vs `player.RulesetHash` check into the fail-closed re-gate (mirroring `MatchAgreementHash`), or document why replay tolerates ruleset drift that live MP does not.
status: open

### DW-431: When a replay ends naturally (reaches its final tick) the playback loop nulls the ReplayPlayer and hides the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-11-replay-v2-tagged-body-scenario-re-gate-replay-ux-browser-playback-perspective.md`
reason: When a replay ends naturally (reaches its final tick) the playback loop nulls the ReplayPlayer and hides the overlay but never resets the fog perspective, so a viewer who cycled to a single-player perspective is left with that player's fog-of-war applied to the frozen final frame until they press F5 / return-to-Edit. — Evidence: `MainScene._Process`'s replay-finished branch tears down the player and overlay but does not reset `_replayPerspective` / restore `FogBridge.RevealAll`; those resets only run on the F5 return-to-Edit path (~line 2118-2135) and in `BeginReplayPlaybackSession`. View-only (no sim/checksum impact), so cosmetic-severity, but the final frame shows a stale, misleading fogged view. Closure = on natural finish, reset `_replayPerspective` and restore the non-replay fog default alongside the existing teardown.
status: open

### DW-432: `ReplayRecorder.RecordTick` silently returns (drops the sub-bundle) when a tick accumulates more than…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-11-replay-v2-tagged-body-scenario-re-gate-replay-ux-browser-playback-perspective.md`
reason: `ReplayRecorder.RecordTick` silently returns (drops the sub-bundle) when a tick accumulates more than `MERGED_MAX_SUBBUNDLES` per-faction sub-bundles, so if the player-slot count ever grows past that ceiling a faction's orders would be silently omitted from the recording — the exact silent-drop the rest of the format is fail-closed against. — Evidence: `MERGED_MAX_SUBBUNDLES == FactionRegistry.PLAYER_COUNT == 8` today, so the drop is unreachable in current ≤8-slot play (latent, not a live bug). But it is an unguarded silent `return` in the one component whose stated invariant is "never silently discard". Closure = assert/throw on the overflow (e.g. `Debug.Assert`) rather than silently returning, so a future >8-slot mode fails loud instead of producing a divergent replay.
status: open

### DW-199: Follow-up review still recommended for 9-11-replay-v2-tagged-body-scenario-re-gate-replay-ux-browser-playback-perspective after the damping cap was spent
origin: review-budget-followup
source_spec: `spec-9-11-replay-v2-tagged-body-scenario-re-gate-replay-ux-browser-playback-perspective.md`
severity: low
reason: The follow-up-review damping cap (limits.max_followup_reviews = 1) was spent with the story finalized (status: done, verify green) while the review pass still recommended an independent follow-up. The work was committed by bmad-loop run 20260724-024337-acea; this entry preserves the lingering recommendation for a deliberate later review.
status: open
decision: 2026-07-27 Schedule the deliberate later review

### DW-433: The server's merged-tick fan-in (`MergedTickBuilder.TryBuild`) waits for ALL Expected slots to submit tick T with…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-13-per-client-command-rate-throttle-anti-spam-on-the-dedicated-server.md`
location: MergedTickBuilder.cs:186
reason: The server's merged-tick fan-in (`MergedTickBuilder.TryBuild`) waits for ALL Expected slots to submit tick T with no timeout or force-advance, so any slot whose tick-T packet never arrives — a mis-set throttle cap, a silent app-level drop, or an uncooperative client — stalls every client on that tick with no recovery short of a transport disconnect (which triggers freeze-and-continue). — Evidence: `MergedTickBuilder.cs:186` returns false and buffers whenever any slot has not arrived; the only silent-recovery path (`DropController`/`FrozenSlotInjector`) fires exclusively on a transport disconnect, never on a still-connected slot with a missing tick. Pre-existing property of the Story 9.3/9.6 backbone (not introduced by 9.13, and not reachable by 9.13 under legitimate play — the [2,12] input-delay pipeline bounds a lockstep client's in-flight backlog far below the 60/window cap). Latent robustness gap worth hardening for the 4-player e2e work (9.15): e.g. a bounded force-advance / missing-slot watchdog so a never-arriving slot degrades to freeze rather than an indefinite stall.
status: open

### DW-434: Only inbound `TickCommands` packets are rate-limited; the other client-sendable dispatch cases on the dedicated…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-13-per-client-command-rate-throttle-anti-spam-on-the-dedicated-server.md`
reason: Only inbound `TickCommands` packets are rate-limited; the other client-sendable dispatch cases on the dedicated server (`Chat`, `LobbyChat`, `Ping`, `DelayAck`, `DropAck`, `Checksum`, and unknown/malformed types) have no per-client throttle, and `Chat`/`LobbyChat` each `BroadcastReliable` to every peer — an amplifying flood vector the command-rate throttle does not cover. — Evidence: `DedicatedServer.HandlePacket`'s `switch (type)` gates only the `TickCommands` arm through `CommandRateLimiter`; every sibling case is unthrottled (pre-existing — none had a limiter before 9.13). Out of scope for 9.13 (a "command-rate throttle" scoped to the command stream, anti-spam not anti-cheat, trusted-friends EA), but a genuine server-wide anti-DoS gap. Closure = a shared per-slot receive-edge limiter applied across the client-sendable packet types (Chat/LobbyChat first, given their broadcast amplification).
status: open
decision: 2026-07-28 correct-course — scheduled follow-up review rides Story 15.5

### DW-200: Host-side (ENet DedicatedServer) StartGame identity enforcement + deterministic in-match deployment of the attested online hero

origin: named follow-up from spec-9-12-server-validated-online-hero-persistence-rail (bmad-loop-resolve decision, Alec, 2026-07-24)
location: godot/src/Multiplayer/DedicatedServer.cs (StartGame path / HandleReady) + godot/src/Multiplayer/NakamaService.cs (MatchFoundInfo, endpoint-only) + godot/src/Multiplayer/Server/AssignedRoster.cs (TryFreeze) + godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs (OnMatchStart mints no hero) + StartStateHash fold
severity: medium
reason: Story 9.12 shipped the server-authoritative storage + validating RPC rail and a live, fail-closed CLIENT launch/Ready attestation gate (`OnlineHeroLaunchGate` over `NakamaService.AttestHeroProfileAsync`), but deliberately did NOT (a) enforce the attestation host-side, nor (b) deploy the attested hero into the lockstep match. Both are blocked on the SAME missing plumbing: the Nakama→ENet handoff (`MatchFoundInfo`) is endpoint-only and the `DedicatedServer` knows peers by transport slot alone — no Nakama userId/token reaches it, and no peer's profile reaches the other peers. Two remaining holes: (1) a friend who patches the game binary to skip the client-side gate (the decisive defense is already the server-owned No-Client-Write storage object + validating RPC — a client cannot forge the stored profile — but the host does not verify the gate ran); (2) minting one peer's hero would diverge `HeroStore` across peers → `StartStateHash` disagreement → online matches would fail to start, so deterministic cross-peer deployment needs a start-state fold this story's `Never`/`Block If` clauses forbid. Fix (post-1.0 fast-follow): add (a) a client→server attestation packet carrying a Nakama-issued credential; (b) server-side Nakama trust so the `DedicatedServer` can verify it; (c) a userId→slot bind in `AssignedRoster.TryFreeze` + a fail-closed gate in `HandleReady`; and (d) server distribution of every peer's attested profile so all peers mint an identical multi-hero `HeroStore` (a deterministic fold, re-baselining `StartStateHash`). 9.12 leaves the `DedicatedServer` StartGame path byte-unchanged and mints no hero into the online match.
status: open

### DW-435: The live online hero rail may be unreachable end-to-end — a first-time online player can only obtain an attested…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-12-server-validated-online-hero-persistence-rail.md`
reason: The live online hero rail may be unreachable end-to-end — a first-time online player can only obtain an attested hero via the picker's Save, which is enabled only when `TryResolvePrimaryHero` finds a placeable hero in the online lobby's scenario; whether a real matchmaking lobby actually presents such a scenario before Ready is unverified, and if it does not, the fail-closed Ready gate soft-locks every player out of every online match. — Evidence: `HeroPickerOverlay.UpdateButtonStates` gates `_saveBtn` on `TryResolvePrimaryHero(...)` (a placed hero for the local faction in `_scenario`), and `LobbyUi.EnsureOnlinePicker` sources that scenario from `ScenarioProvider?.Invoke()` (wired by `HeroPickerPhase` to `_ctx.Scenario`). Online Ready is fail-closed on `_onlineHeroAttested`, set only after a successful Save+attest. All four review layers flagged the reachability risk. It is genuinely runtime-dependent (needs a live two-client Nakama match, which the spec's Boundaries explicitly exclude — "Do not deploy/host Nakama") so it could not be verified in the review sandbox. Closure = a live-Nakama manual verification that a fresh online player can create+attest a hero and Ready, plus (if the pre-match lobby has no placeable-hero scenario) an online-first hero-creation/selection path that does not depend on an in-scenario placed unit.
status: open

### DW-436: The TS server validator runs O(n^2) duplicate-key/duplicate-slot scans over `values`/`inventory` BEFORE any size…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-12-server-validated-online-hero-persistence-rail.md`
reason: The TS server validator runs O(n^2) duplicate-key/duplicate-slot scans over `values`/`inventory` BEFORE any size cap bites (the `MAX_STORED_PROFILE_BYTES` check is applied only after `validateHeroProfile` on the write path, and not at all on the attest read path), so a single large in-range payload can drive a per-request CPU spike on Nakama's single-threaded goja runtime. — Evidence: `docs/server-deploy/nakama-modules/src/main.ts` `handleWriteHeroProfile` calls `validateHeroProfile(profile)` before the `JSON.stringify(sanitized).length > MAX_STORED_PROFILE_BYTES` guard; `validation.ts` `validateHeroProfile` does nested O(n^2) dup scans. `handleAttestHeroProfile` re-validates a stored object with no size cap at all (reachable via a first-time raw client write of an oversized object, since permissionWrite=0 only protects an already-stored object). Low real priority for a self-hosted trusted-friends EA server and likely partly mitigated by Nakama's own RPC request-size limits, but a genuine hardening gap in new code. Closure = a raw-payload / element-count guard at the top of both handlers (handler-only, goja-safe with `String.length`, no impact on the shared C#<->TS parity fixture), and/or O(n) dup detection via a Set.
status: open

### DW-437: The C#<->TS validator parity guarantee ("a shared fixture so the two implementations cannot silently drift") is…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-12-server-validated-online-hero-persistence-rail.md`
reason: The C#<->TS validator parity guarantee ("a shared fixture so the two implementations cannot silently drift") is only half-enforced automatically — the C# `HeroProfileValidatorTests` run in CI (`determinism-gate.yml`), but the TS `validation.test.ts`/`handlers.test.ts` (vitest) run only via the README's manual `npm test`, so a `validation.ts` regression that diverges from the shared oracle ships with the CI suite green. — Evidence: `.github/workflows/determinism-gate.yml` is the only workflow and every job runs `dotnet test`/analyzer projects — no npm/node/vitest step. `package.json` `test` script (`vitest run`) is invoked only by the spec's manual Verification command and the README. The C# side (`SharedFixture_CSharpValidatorMatchesOracle`, `XpCeilingRaw_MatchesSharedFixtureConstant`) passes regardless of any TS drift because it exercises only the C# validator. Closure = add a vitest step to the CI gate (`npm --prefix docs/server-deploy/nakama-modules ci && npm --prefix ... test`) so a TS validator regression fails the build.
status: open

### DW-438: The parity fixture proves the two validators agree on hand-authored JSON literals, but no test feeds a genuinely…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-12-server-validated-online-hero-persistence-rail.md`
location: PlayerProfile.cs
reason: The parity fixture proves the two validators agree on hand-authored JSON literals, but no test feeds a genuinely C#-serialized `PlayerProfile` (`JsonSerializer.Serialize`) to the TS validator/handlers, so a wire-format drift on the live client->server boundary (a `JsonPropertyName` rename, a `Fixed` encoding change, a new nested converter) could break `rpc_write_hero_profile`/`rpc_attest_hero_profile` at runtime with all rule-parity tests green. — Evidence: `HeroProfileValidatorTests` deserializes the shared fixture JSON into `PlayerProfile` but never serializes one back for the TS side; `validation.test.ts`/`handlers.test.ts` read the same hand-written JSON. `PlayerProfile.cs` uses snake_case `JsonPropertyName` + `ProfileInventoryItemJsonConverter` — aligned by construction but unasserted at the wire level. Residual risk is reduced (the same serialization also feeds the offline `profiles.json` persistence/golden tests, which would catch a shape rename), which is why this is a lower-priority hardening item rather than a correctness bug. Closure = a round-trip test that serializes a real `PlayerProfile` in C# and asserts the exact bytes are accepted by the TS validator/handler shape.
status: open

### DW-439: `AiOpponentSystem` target/raze/threat selection is not alliance-aware, so in a teamed match an AI will attack (and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/AI/AiOpponentSystem.cs
reason: `AiOpponentSystem` target/raze/threat selection is not alliance-aware, so in a teamed match an AI will attack (and its army will march on) an allied faction's units and buildings while allied units — now correctly excluded by combat — never retaliate. — Evidence: `godot/src/AI/AiOpponentSystem.cs` (~lines 178, 215, 470) classifies hostiles as `f == AI_FACTION || f == Faction.Neutral`, never consulting `AllianceStore.AreAllied`, unlike the combat/spatial-hash sites this story made alliance-aware. Not in this story's combat/vision/victory intent scope, and AI-in-MP-teams is additionally gated on Story 10.13 + AI float→Fixed determinism (both deferred). Real for single-player skirmish teams-with-AI. Closure = thread `AllianceStore` into `AiOpponentSystem` and replace the faction-equality hostility checks with `AreAllied`, matching the combat convention.
status: open

### DW-440: `SelectionSystem` right-click pickers classify allied units/buildings as enemies and issue a force…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/UI/SelectionSystem.cs
reason: `SelectionSystem` right-click pickers classify allied units/buildings as enemies and issue a force Attack/AttackBuilding order that the new combat guard silently rejects, so right-clicking an ally in a teamed match is a dead no-op (no move, no follow, no attack). — Evidence: `godot/src/UI/SelectionSystem.cs` `FindNearestEnemyUnit` (~1027) and `FindNearestEnemyBuilding` (~1073) define enemy as `f == me [|| Neutral]` with no alliance check; the resulting force order hits the new `CombatSystem` allied-force-fire rejection and reverts to Idle. Presentation-layer (Player1-hardcoded, local player only) and not named in the story's ACs, but a guaranteed-hit UX regression in the very 2v2 scenario this story enables. Closure = thread `AllianceStore` into `SelectionSystem` so a right-click on an ally routes to Move/Follow instead of a rejected force-attack.
status: open

### DW-441: `FogOfWarSystem.SharedTeamVision` has no user-facing control — it defaults true and is set nowhere in production…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/Core/FogOfWarSystem.cs
reason: `FogOfWarSystem.SharedTeamVision` has no user-facing control — it defaults true and is set nowhere in production, so every teamed match is forced into shared allied vision with no way to disable it, and the AC's "toggle" is a code property only. — Evidence: `godot/src/Core/FogOfWarSystem.cs` exposes `bool SharedTeamVision` (default true); grep finds only test setters, no lobby/settings wiring. Behavior is present and correct (default-on satisfies "when enabled"), so this is a presentation follow-up, not a correctness gap. Closure = wire `SharedTeamVision` to a lobby/settings toggle passed through `MatchLifecycleController`/`MainScene`.
status: open

### DW-442: Scenario team authoring has no validation — a scenario putting all active slots on one team (combat then excludes…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/Core/Definitions/ScenarioValidator.cs
reason: Scenario team authoring has no validation — a scenario putting all active slots on one team (combat then excludes everyone → no acquisition/eliminations → degenerate/hung match), a duplicated `.Slot` (order-dependent canonical team id), an out-of-range team member (silently dropped, no log), and an inert team-of-one all ship without author-facing feedback. — Evidence: `godot/src/Core/Definitions/ScenarioValidator.cs` (player-slots loop ~230-264) validates `Slot`/ore/coords/uniqueness but never `Team`; `godot/src/Core/AllianceSeeder.cs` `ComputeTeamIds` (~706-731) drops `faction >= FACTION_COUNT` silently (unlike `ScenarioApplier`'s warning) and is last-write-wins on duplicate `.Slot`. These layouts became authorable only with this story's new `Team` field. Closure = add `ScenarioValidator` team checks (>=2 distinct live teams among active slots; reject/warn on duplicate `.Slot` and inert team-of-one) and a diagnostic in `AllianceSeeder` for out-of-range team members.
status: open

### DW-443: `Team` is deliberately folded only into `MatchAgreementHash` (excluded from `CanonicalModelHash`), so any…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/Core/Definitions/ScenarioData.cs
reason: `Team` is deliberately folded only into `MatchAgreementHash` (excluded from `CanonicalModelHash`), so any match-start/peer-agreement path that gates on `CanonicalModelHash` alone rather than the agreement hash would not detect a team-only disagreement between peers → divergent `AllianceStore` seeds → tick-0 desync — the exact failure class this story claims to fail closed on. — Evidence: `godot/src/Core/Definitions/ScenarioData.cs` Team doc + `MatchAgreementHash.cs` fold; `HandshakeGate`/`ServerLobbyPolicy.CheckStartStateAgreement` compare the agreement hash, but no test asserts that EVERY start path compares it. Closure = audit all start paths to confirm each compares `MatchAgreementHash`, and add a start-path test asserting a team-only difference between two peers is rejected before tick 0.
status: open

### DW-444: Projectile primary/direct-hit damage is not alliance-rechecked at impact (only `ApplySplash` is), so if a primary…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/Combat/ProjectileSystem.cs
reason: Projectile primary/direct-hit damage is not alliance-rechecked at impact (only `ApplySplash` is), so if a primary target's entity id is recycled into an allied entity between projectile spawn and impact, the projectile still damages the now-ally — asymmetric with the splash guard. — Evidence: `godot/src/Combat/ProjectileSystem.cs` `ApplySplash` (~169) skips `AreAllied(owner, victim)` but the primary-target delivery path re-checks only `IsAlive`, not alliance; `CombatSystem`'s force-fire comment flags id-recycle as reachable. Obscure edge (target acquisition already excludes allies at spawn time) and a pre-existing damage-delivery class. Closure = recheck `!AreAllied(owner, FactionOf[primaryTarget])` at impact for symmetry, or document the site as out of scope with the recycle reasoning.
status: open

### DW-445: `AiOpponentSystem`'s target/raze pickers classify any non-AI, non-Neutral faction as an enemy with no…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/AI/AiOpponentSystem.cs
reason: `AiOpponentSystem`'s target/raze pickers classify any non-AI, non-Neutral faction as an enemy with no `AllianceStore` check, so an AI placed on a team with a player perpetually orders `AttackBuilding`/attack onto its ally — orders the new `CombatSystem` allied guard then rejects, leaving the AI's force stuck at Idle instead of engaging the real enemy. — Evidence: `godot/src/AI/AiOpponentSystem.cs` `FindNearestEnemyBuilding` (~462) / `DoRazeBuildings` (~436) skip only `f == AI_FACTION || f == Faction.Neutral`; a teamed AI's chosen target hits `CombatSystem`'s Story-9.14 `AreAllied` force-attack rejection (`CombatSystem.cs:332`) and reverts to Idle. Distinct from the existing AI float-determinism defers (2026-06-09 scorer + the MP AI-takeover entry) which gate AI in lockstep MP but do not cover single-player skirmish AI-on-a-team target selection. Closure = thread `AllianceStore` into `AiOpponentSystem` and skip `AreAllied(AI_FACTION, f)` in both pickers (mirroring the `CombatSystem` building-scan change), OR reject AI-faction-on-a-team in `ScenarioValidator`. Gated behind the existing AI float→Fixed determinism prerequisite before any teamed-AI MP use.
status: open

### DW-446: A HELD auto-acquired unit `AttackTarget` is not alliance/faction-rechecked while retained —…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `{implementation_artifacts}/spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
location: godot/src/Combat/CombatSystem.cs
reason: A HELD auto-acquired unit `AttackTarget` is not alliance/faction-rechecked while retained — `CombatSystem.ValidateOrClearTarget` clears only on the target's death — so if that target's entity id is recycled into an allied (or same-faction) unit between ticks, the attacker fires on the now-ally for a tick, violating "an ally is never auto-attacked." — Evidence: `godot/src/Combat/CombatSystem.cs` `ValidateOrClearTarget` (~554-563) drops a held `AttackTarget` only when `!IsAlive`; Story 9.14 added allied-exclusion only at acquisition (`FindNearestEnemy*`, threaded `_alliances`) and on the per-tick forced paths (force-fire ~900, `TickAttackBuildingCombat` re-checks alliance every tick). Entity slots are shared across factions in one `EntityWorld`, so a teammate training a unit into a freed enemy slot in a 2v2 is reachable. Distinct from the existing projectile-primary-recycle defer (that entry is `ProjectileSystem.ApplySplash`/primary impact; this is the CombatSystem held unit-target path). The underlying recycle-into-friendly gap also pre-exists for same-faction (the force-fire comment at ~274-276 acknowledges it), so a holistic fix should cover both same-faction and allied. Obscure edge (one-tick window on a same-tick slot recycle). Closure = make `ValidateOrClearTarget` an instance method that also clears when `_alliances.AreAllied(FactionOf[id], FactionOf[target])` (and, for the pre-existing class, same-faction), mirroring the per-tick forced-path guards.
status: open
decision: 2026-07-28 correct-course — keep open; post-1.0 fast-follow per bmad-loop-resolve 2026-07-24

### DW-201: Follow-up review still recommended for 9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model after the damping cap was spent
origin: review-budget-followup
source_spec: `spec-9-14-teams-alliances-lobby-teams-wired-into-the-sim-alliance-model.md`
severity: low
reason: The follow-up-review damping cap (limits.max_followup_reviews = 1) was spent with the story finalized (status: done, verify green) while the review pass still recommended an independent follow-up. The work was committed by bmad-loop run 20260724-143824-31e0; this entry preserves the lingering recommendation for a deliberate later review.
status: open
decision: 2026-07-27 Schedule the deliberate later review

### DW-447: `LoopbackDesyncSelfTest` sends a hardcoded `GOOD` checksum rather than each peer's computed sim checksum, so no…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-15-four-player-verified-end-to-end.md`
reason: `LoopbackDesyncSelfTest` sends a hardcoded `GOOD` checksum rather than each peer's computed sim checksum, so no automated artifact runs N independent sims that compute + compare their own checksums (the literal "4-client zero-desync end-to-end" surface is proven only by the manual two-machine LAN runbook, not headlessly). — Evidence: Story 9.15's headless e2e feeds one in-process `SimulationHost`'s single checksum to all four `ServerHost(4)` slots (unanimous by construction), and `LoopbackDesyncSelfTest` (the only real-transport 4-peer artifact) sends a fixed `GOOD` hash — so the literal AC1 "4-client loopback ... zero desync" surface (four independent sims agreeing across real transport, plus rendered lobby/score screens) is exercised by neither automated path; the in-engine render gate was left optional/unrun per the spec's headless split.
status: open

### DW-448: The high-severity `MainScene.ShowGameOver` local-win headline logic (VICTORY iff the LOCAL faction's OWN latched…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-15-four-player-verified-end-to-end.md`
location: GameOverSummaryTests.cs
reason: The high-severity `MainScene.ShowGameOver` local-win headline logic (VICTORY iff the LOCAL faction's OWN latched verdict is WON, and the "Team Victory — …" sub-line) has no automated regression net — only the extracted `GameOverSummary.Build` data rows are unit-tested, so a revert to the old `winnerPlayer == 1` keying (the exact 2v2 "winning ally sees DEFEAT" bug fixed in the prior review pass) would pass every test. — Evidence: `GameOverSummaryTests.cs` asserts only `GameOverSummary.Build` row/verdict/color; the local-win decision at `MainScene.cs:1575-1600` (`localFaction = _ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1; localWin = WinState.Verdict[(int)localFaction] == VERDICT_WON`) and the `wonFactions.Count > 1` team sub-line live in a Godot-coupled node with zero automated coverage. The spec's Design Notes deliberately route score-screen verification to the data layer + optional in-engine `/godot-verify`, so this is a coverage gap consistent with intent, not a shipped defect — the rendered code is correct per all reviewers. Closure = extract the headline decision (localWon + team-victory phrasing) into a Godot-free helper and Tier-1 assert it (teams {1,1,2,2}, P1/P2 WON, local seat P2 → VICTORY; >1 WON → "Team Victory"), mirroring the `GameOverSummary` extraction the story already established.
status: open

### DW-449: The shared effect-tree walk (`CanonicalFold.MixEffect`/`MixModifier`) folds a hand-maintained subset of each…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-9-16-full-content-pre-match-hash-handshake.md`
reason: The shared effect-tree walk (`CanonicalFold.MixEffect`/`MixModifier`) folds a hand-maintained subset of each `EffectNode`/`Modifier` field and its `default` case folds only the runtime type name (value-blind), with NO reflection completeness guard over the effect subtypes — so a new effect field or a brand-new effect kind moves the sim but not the handshake hash, letting a modded ability/item effect pass the gate and desync mid-match. — Evidence: `CanonicalFold.MixEffect`'s `default:` arm folds `e.GetType().Name` only (zero fields, no recursion); the def POCOs are guarded by `ContentFoldCompletenessTests` but effect subtypes are not. PRE-EXISTING — the walk was moved verbatim from `CanonicalModelHash` (same behavior for DSL `run_effect` embeds since v8), so this change did not introduce it; it only widens the blast radius to ability/item `EffectGraph`s (Story 9.16). Closure = a reflection completeness guard over `EffectNode`/`Modifier` subtypes (analogous to the def guard), or fail-closed on an unrecognized effect kind in the content-fold path instead of a value-blind name fold.
status: open

### DW-450: `FactionDefinition.GetBuilding` still NREs on a null `Buildings` list or a null building element — the exact…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-content-validator-hardening.md`
location: godot/src/Core/Definitions/FactionDefinition.cs
reason: `FactionDefinition.GetBuilding` still NREs on a null `Buildings` list or a null building element — the exact malformed-but-parseable JSON class (`"buildings": null` / `[null, {...}]`) that DW-103 just hardened on the four *unit* getters — leaving the buildings lookup path asymmetrically unguarded. — Evidence: `godot/src/Core/Definitions/FactionDefinition.cs` `GetBuilding` (:94-98) is `foreach (var b in Buildings) if (b.Id == id)` with no `Buildings == null` guard and no `b != null` guard, unlike the sibling `GetUnit`/`IndexOfUnit`/`GetUnitByCategory`/`GetUnitsByCategory` (now null-safe) and `GetResearch` (already null-safe). Two independent reviewers flagged it, and this story's own round-trip test calls `GetBuilding`. Genuinely pre-existing and explicitly out of scope for this story — the intent's Boundaries state "Do NOT touch `PrimaryUnit`/`GetBuilding`" — so it was correctly not fixed here. Closure = mirror the DW-103 unit-getter fix (`if (Buildings == null) return null; foreach (var b in Buildings) if (b != null && b.Id == id) return b;`).
status: open

### DW-451: `TechTreePanel.RebuildGraph`'s building loop dereferences `b.Id` with no null-element guard, so a null building…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-content-validator-hardening.md`
location: godot/src/CreationSuite/TechTreePanel.cs
reason: `TechTreePanel.RebuildGraph`'s building loop dereferences `b.Id` with no null-element guard, so a null building element mid-edit NREs the graph rebuild — the same null class the DW-89 presentation fix guarded in the sibling `OnNodeSelected` and that `RebuildGraph`'s own research loop already guards. — Evidence: `godot/src/CreationSuite/TechTreePanel.cs` `RebuildGraph` (~:218-219) iterates `_faction.Buildings` and assigns `buildingById[b.Id] = b` without a `b != null` check, while the adjacent research loop (~:260) guards `r != null` and the just-hardened `OnNodeSelected` (:438) now uses `b != null && b.Id == id`. Pre-existing Godot-presentation NRE (not Tier-1 testable), out of this story's seven-DW scope. Low reach (requires a null building element in the in-memory faction mid-edit). Closure = add `if (b != null && !string.IsNullOrEmpty(b.Id))` to the RebuildGraph building loop, matching its research loop.
status: open

### DW-452: The item editor's Speed spinner clamp to ±MAX_MOVE_SPEED_DELTA (this story's AC4) has no automated test — it rides…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `spec-item-definition-validator-hardening.md`
location: ItemCardPanel.Edit.cs:49
reason: The item editor's Speed spinner clamp to ±MAX_MOVE_SPEED_DELTA (this story's AC4) has no automated test — it rides entirely on the shared constant `MoveSpeedCap = MAX_MOVE_SPEED_DELTA.ToInt()`, so a future decoupling of the spinner's min/max from the constant would silently violate the AC with no failing test. — Evidence: `ItemCardPanel.Edit.cs:49` sets the "Speed" `AddNumFloat` range to `-MoveSpeedCap, MoveSpeedCap`, but a grep for `AddNumFloat`/`MoveSpeedCap`/`ItemCardPanel` across `**/*Test*.cs` returns nothing — the panel is a Godot Control (presentation layer) untestable in the Godot-free `ProjectChimera.Sim.Tests`. Safety is preserved regardless because `ValidateFields` fail-closes any over-cap value (badges + disables Save), so this is a UX-clamp verification gap, not a fail-open hole — consistent with the project's deferral of Godot-UI verification to the GdUnit4 (Tier-2) tier. Closure = a GdUnit4 test that instantiates `ItemCardPanel` and asserts the "Speed" SpinBox MinValue/MaxValue == ±MAX_MOVE_SPEED_DELTA, or extract a Godot-free helper returning the spinner range that a Tier-1 test pins.
status: open

### DW-453: The item editor mints ids through a LOCAL `SanitizeId` (Unicode `char.IsLetterOrDigit`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `spec-item-definition-validator-hardening.md`
location: ItemCardPanel.Edit.cs
reason: The item editor mints ids through a LOCAL `SanitizeId` (Unicode `char.IsLetterOrDigit`, `ItemCardPanel.Edit.cs:314`) that diverges from `UnitDefinitionValidator.SanitizeId` (ASCII `[a-z0-9_]`), so DoCreate/DoDuplicate from a base with a Unicode letter/digit (e.g. `café`) produces an id the local sanitizer keeps but the newly-added `ValidateFields` charset gate rejects — an un-saveable item needing a manual rename. — Evidence: `ItemCardPanel.Edit.cs` `SanitizeId` (:314-320) uses `char.IsLetterOrDigit` (Unicode-aware), while `UnitDefinitionValidator.SanitizeId` used by the new DW-47 gate is ASCII-only; before this story `ValidateFields` did not charset-check ids, so the divergence was latent — this change surfaced it. Fail-closed (a bypass is impossible; the gate still rejects), so it is a UX inconsistency, not a security gap. The local sanitizer predates this story. Closure = have the editor's `UniqueId`/`SanitizeId` delegate to `UnitDefinitionValidator.SanitizeId` (the single shared convention DW-47 intended), so minted ids always satisfy the gate.
status: open

### DW-454: The filename-safe charset check admits Windows reserved device basenames (`con`, `nul`, `aux`, `prn`, `com1`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `spec-item-definition-validator-hardening.md`
location: ItemDefinitionValidator.cs:90
reason: The filename-safe charset check admits Windows reserved device basenames (`con`, `nul`, `aux`, `prn`, `com1`, `lpt1`) — all within `[a-z0-9_]` — so saving such an item makes `Persist()`'s `File.WriteAllText("con.json.tmp")` throw on Windows (the primary platform), surfacing as an opaque generic "Save failed" with no field badge and no way to save the item. — Evidence: `ItemDefinitionValidator.cs:90` / `:154` check only the `[a-z0-9_]` charset, which reserved device names satisfy; `Persist()` (`ItemCardPanel.Edit.cs:223`) then writes `<id>.json.tmp`, which the Win32 filesystem rejects for reserved basenames, caught only by the generic `catch (Exception ex)` → "Save failed". Pre-existing class orthogonal to DW-47's traversal fix and shared with the sibling `UnitDefinitionValidator` charset. Niche (requires a creator to name an item exactly a reserved word). Closure = add a reserved-basename reject (`con|prn|aux|nul|com[1-9]|lpt[1-9]`) alongside the charset check in both validators.
status: open

### DW-455: `ItemRegistry.LoadFromDirectory` silently drops any item whose def fails `Validate` (the `else…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `spec-item-definition-validator-hardening.md`
location: ItemRegistry.cs:70-71
reason: `ItemRegistry.LoadFromDirectory` silently drops any item whose def fails `Validate` (the `else onSkipped?.Invoke(...)` branch), and this story's newly-tightened id charset (`SanitizeId`, which also lowercases/trims) and ±50 move-speed cap widen what now fails — so a hand-authored/community item JSON with a mis-cased/hyphenated id or `move_speed_delta` in (50,1000] vanishes at load with no user-visible diagnostic unless `onSkipped` is wired to a warning at the call site. — Evidence: `ItemRegistry.cs:70-71` — `if (r.Ok) defs.Add(r.Value.Value); else onSkipped?.Invoke(Path.GetFileName(file));` — the drop is unconditional and `onSkipped` is optional. No shipped content regresses today (the only item def, `ring_of_vigor.json`, has a clean id and `move_speed_delta: 0`), so this is latent, not a live break. Pre-existing registry behavior surfaced incidentally by the contract tightening; the tightening itself was directed by the intent. Closure = verify MainScene's `LoadFromDirectory` call wires `onSkipped` to a visible warning listing dropped item files (and consider the same for the move-speed migration).
status: open

### DW-456: The DW-47 traversal-id charset guard on `ItemCardPanel.DoDelete`'s `File.Delete` (and the Save→`Persist()` gating…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `spec-item-definition-validator-hardening.md`
location: ItemCardPanel.Edit.cs:282-290
reason: The DW-47 traversal-id charset guard on `ItemCardPanel.DoDelete`'s `File.Delete` (and the Save→`Persist()` gating for `Path.Combine`/`File.Move`) has no automated coverage — `ItemCardPanel` is a Godot `Node` outside the Tier-1 Godot-free test boundary, so the sole guard against a hand-typed traversal id reaching a filesystem sink is verified only by reading; deleting the `if (SanitizeId(id) == id)` line would reopen the sink with every test still green. — Evidence: `ItemCardPanel.Edit.cs:282-290` — the delete decision lives inline in a `dlg.Confirmed +=` lambda in a `: Node` class; a repo-wide search finds no test referencing `ItemCardPanel`. The validator-surface tests (`TraversalId_FailsClosed_SimGate`, `TraversalId_IsKeyedError`) assert only the validator's return value, never the sink. Pre-existing structural testability limitation (Godot presentation tier). Closure = extract the "may this id touch the on-disk file?" decision into a Godot-free static predicate (e.g. `ItemDefinitionValidator.IsFilenameSafeId`) reused by both charset gates + `DoDelete`, and unit-test it Tier-1; absent that, add an in-engine `/godot-verify` step.
status: open
decision: 2026-07-28 correct-course — scheduled follow-up review rides Story 15.5

---

# Migrated legacy items (correct-course 2026-07-28)

_The freeform pre-DW numbered sections were verified item-by-item against the codebase on 2026-07-28 (8 parallel evidence-backed verification passes) and migrated below as canonical DW entries DW-202..DW-324, preserving original file order. Full analysis: `_bmad-output/planning-artifacts/sprint-change-proposal-2026-07-28.md`._

## From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review) — migrated 2026-07-28

### DW-202: AI wave logic counts zero-damage units as combat units and leaks them from the available pool
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/AI/AiOpponentSystem.cs:222-225,411-454
severity: high
reason: BuildSnapshot's availability loop and DoLaunchAttack/DoRazeBuildings still have no `EffectiveAttackDamage > Fixed.Zero` filter (the 2.13-added filter at :218 covers only the EnemyThreatRemains branch). A zero-damage non-gatherer flipped to AttackMove is skipped by CombatSystem's non-combatant gate (CombatSystem.cs:126) and never exits the command — permanently leaked from the AI's available pool. Reachable via trigger spawn_unit of a zero-damage authored unit into the AI slot. Verified still-live 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle noncombatant-command-gate (Epic 15, Story 15.4); fix together with DW-206/DW-242 (one gate hoist + filter)

### DW-203: Rally points are not lockstep-replicated
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/UI/SelectionSystem.cs
severity: critical
reason: SelectionSystem wrote HasRallyPoint/RallyPoint locally (no EnqueueOrder, not in replays) — a desync vector via SpawnTrainedUnit's rally branch.
status: done 2026-07-28
resolution: Story 2.12 — SetRallyPoint (SelectionSystem.cs:955-968) routes through `_lockstep.EnqueueOrder(UnitCommand.SetRally)` + shared OrderApplier; rally state folded into SimChecksum v9 (SimChecksum.cs:50-51); pinned by CommandApplyParityTests replay round-trip. Verified 2026-07-28.

### DW-204: Float math in the AI utility scorer + hardcoded AI building costs
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/AI/AiOpponentSystem.cs:34-37,54-55,242-351
severity: high
reason: All Score* methods still use float/Math.* (Story 2.13 Decision 4 explicitly declined the migration); illegal in lockstep MP until converted. Companion: AI building costs are hardcoded `Fixed.FromFloat` constants at :34-37 that must mirror EntityPlacer.BUILDING_COSTS and faction JSON — no single source. Verified still-live 2026-07-28.
status: open
decision: 2026-07-28 correct-course — owned by Story 10.11 (AI float→Fixed); hardcoded-cost data-sourcing added to 10.11's scope note

### DW-205: SpawnTrainedUnit never sets GatherState/CarryCapacity — trained workers never gather
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/Economy/BuildingSystem.cs:183-280
severity: high
reason: SpawnTrainedUnit ends at the rally/Stop branch without writing the caller-owned GatherState/CarryCapacity residue fields. Previously latent (CC production blocked); NOW REACHABLE since Story 6.8 — an authored custom building with `produces_category: "Worker"` trains workers that never gather and are combat-active. The correct 4-line reference implementation exists at ScenarioApplier.SpawnUnitAt (ScenarioApplier.cs:493-497). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle trained-worker-gather-init (Epic 15, Story 15.4)

### DW-206: UnitCommand.Build falls to CombatSystem's default case (TickIdleCombat)
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/Combat/CombatSystem.cs:164-166
severity: low
reason: Build still routes to `default:` — latent (only workers receive Build today, and gatherers exit earlier), but the guard is `GatherState != Inactive`, not "is a worker": a non-gatherer that ever receives Build auto-chases enemies. One-line `case UnitCommand.Build: continue;`. Verified still-live 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle noncombatant-command-gate (Epic 15, Story 15.4)

### DW-207: AssignedGatherers leaks on worker death — nodes permanently lose gatherer capacity
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/Economy/GatheringSystem.cs:320-326; godot/src/Economy/BuildingSystem.cs:784
severity: high
reason: ReleaseNode is only called from the en-route node-vanished branch; the main loop skips dead entities (:49) and no EntityWorld.OnDestroy subscriber exists for gathering (only ModifierStore + ItemSystem subscribe). Second leak site: the Build-interrupt at BuildingSystem.cs:784 clears GatherTarget without decrementing. Peer-identical (AssignedGatherers IS folded, v13) — a gameplay/capacity defect, not a desync. Fix will move goldens (folded counter changes) → isolated re-baseline per the checksum-fold timing rule. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle gatherer-slot-release-on-death (Epic 15, Story 15.4); golden re-baseline expected

### DW-208: AttackMove arrive threshold unreachable under crowding (wave hover deadlock)
origin: migrated from legacy ledger ("From spec-ai-deadlock-combat-gathering-fix (2026-06-09 review)"), 2026-07-28
location: godot/src/Combat/CombatSystem.cs:79-84; godot/src/Core/ArrivalTuning.cs
severity: high
reason: AMOVE_ARRIVE_SQR = 0.5u² held an equilibrium ring at ~1.0u so converging waves never "arrived".
status: done 2026-07-28
resolution: Story 2.13 — AMOVE_ARRIVE_SQR now reads ArrivalTuning.GoalArriveRadiusSqr (2u/4u², single-sourced with ORDER_ARRIVE_SQR; class doc names this deferral); pinned by ArrivalRadiusTests (AttackMoveWave_ConvergingOnOneGoal_AllReachIdle). Caveat: zero-damage units remain excluded by the pre-switch gate — that residue is DW-202/DW-242. Verified 2026-07-28.

## From code review of story-1.1 (2026-06-22) — migrated 2026-07-28

### DW-209: Determinism boundary tests missing for Fixed
origin: migrated from legacy ledger ("From code review of story-1.1 (2026-06-22)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Determinism/FixedBoundaryTests.cs
severity: high
reason: FixedSmokeTests was happy-path only (no negative-multiply rounding, division truncation, 16.16 overflow, Sqrt edge cases).
status: done 2026-07-28
resolution: Story 1.2 — FixedBoundaryTests.cs covers exactly the deferred cases (Multiply_NegativeProduct_FloorsAwayFromZero, Divide_NegativeResult_TruncatesTowardZero, overflow + Sqrt sections); header comment names this deferral. Verified 2026-07-28.

### DW-210: No structural guard for the Godot-free folder contract
origin: migrated from legacy ledger ("From code review of story-1.1 (2026-06-22)"), 2026-07-28
location: godot/SimSources.props
severity: medium
reason: Sim.Tests globbed sim folders with hand-named removes; a future `using Godot;` file would break the shared-source build far from source.
status: done 2026-07-28
resolution: Story 1.10b — SimSources.props is the single source imported by BOTH Sim.Tests and Sim.Analysis (analyzer coverage cannot drift from the tested set); BannedSimApiAnalyzer CHM0001-0005 + GodotFreeBoundaryTest; CI job tier1-analyzer-gate. Residual (accepted): excludes still hand-named, no folder-set-equality test. Verified 2026-07-28.

### DW-211: Sim.Tests runs in no routine build/CI
origin: migrated from legacy ledger ("From code review of story-1.1 (2026-06-22)"), 2026-07-28
location: .github/workflows/determinism-gate.yml
severity: high
reason: The Godot-free boundary test only executed on manual `dotnet test`.
status: done 2026-07-28
resolution: Story 1.10a/1.10c — determinism-gate.yml jobs tier1-golden-gate (Windows) and tier1-golden-gate-linux run the full Tier-1 suite on every push. Verified 2026-07-28.

### DW-212: No packages.lock.json — restore not bit-reproducible
origin: migrated from legacy ledger ("From code review of story-1.1 (2026-06-22)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/packages.lock.json
severity: medium
reason: Transitive deps floated.
status: done 2026-07-28
resolution: Story 1.10a — lock files exist for Sim.Tests + Analyzers; all CI restores use --locked-mode with SDK pinned via CI-only global.json (8.0.419). Verified 2026-07-28.

### DW-213: CS8632 nullable annotations without #nullable enable (2 files remain)
origin: migrated from legacy ledger ("From code review of story-1.1 (2026-06-22)"), 2026-07-28
location: godot/src/Economy/GatheringSystem.cs:34-36; godot/src/Navigation/FlowFieldSystem.cs:114
severity: low
reason: Of the original 6 warnings, SimulationLoop.cs got `#nullable enable` (3 cleared); GatheringSystem (`MatchStats?`) and FlowFieldSystem (`out FlowField?`) still lack the directive. Two-line fix. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle nullable-directive-cleanup (Epic 15, Story 15.5)

## From code review of story-1.3a (2026-06-23) — migrated 2026-07-28

### DW-214: FactionRegistry accepts more players than the stores can hold
origin: migrated from legacy ledger ("From code review of story-1.3a (2026-06-23)"), 2026-07-28
location: godot/src/Core/FactionRegistry.cs; godot/src/Economy/ResourceStore.cs:12
severity: high
reason: Faction enum stopped at Player4 while the ctor accepted 8; constructing >4 indexed out of bounds.
status: done 2026-07-28
resolution: Story 9.2 — PLAYER_COUNT=8, every store sized from FACTION_ARRAY_SIZE=9 (ResourceStore/MatchStats/ResearchStore/WinStateStore/AllianceStore); FactionRegistryTests re-pinned (Ctor_AcceptsTheInclusiveBounds(8) now genuinely valid, Ctor_RejectsOutOfRangeActiveCount(9) is the ceiling); MultiFaction8Scenario exercises end-to-end. Verified 2026-07-28.

## From code review of story-1.3b (2026-06-23) — migrated 2026-07-28

### DW-215: Fixed.FromFloat residual at the ScenarioDirector compare sites
origin: migrated from legacy ledger ("From code review of story-1.3b (2026-06-23)"), 2026-07-28
location: godot/src/Core/ScenarioDirector.cs
severity: medium
reason: Authored thresholds were converted at the compare site; ≥32768 wrapped negative.
status: done 2026-07-28
resolution: Story 1.4 — zero FromFloat calls remain in ScenarioDirector (compare sites read already-Fixed authored values); TriggerDefinition.Amount is `Fixed` parsed via FixedJsonConverter which rejects |v| ≥ 32768 with a located JsonException (FixedJsonConverter.cs:63-67) — both facets closed. Verified 2026-07-28.

## From code review of story-1.4 (2026-06-23) — migrated 2026-07-28

### DW-216: FixedJsonConverter.Write is lossy (ToFloat), no round-trip test
origin: migrated from legacy ledger ("From code review of story-1.4 (2026-06-23)"), 2026-07-28
location: godot/src/Core/Definitions/FixedJsonConverter.cs:77-78
severity: low
reason: Write still emits `value.ToFloat()` (24-bit mantissa); save→load→save can shift large/deep-fractional authored values ≥1 raw unit. Not a desync (peers load identical bytes); authoring fidelity only.
status: done 2026-07-28
resolution: Closed as accepted (correct-course 2026-07-28) — recorded design tradeoff; revisit only if a "no silent value drift on re-save" guarantee becomes a creation-suite requirement.

### DW-217: JSON-omitted Fixed fields bypass the converter (future fractional defaults)
origin: migrated from legacy ledger ("From code review of story-1.4 (2026-06-23)"), 2026-07-28
location: godot/analyzers/ProjectChimera.Analyzers/BannedSimApiAnalyzer.cs:97-104
severity: low
reason: A future `= Fixed.FromFloat(1.5f)` field initializer would be an unguarded quantization outside the boundary.
status: done 2026-07-28
resolution: Story 1.10b — CHM0005 flags any FromFloat/ToFloat outside FixedJsonConverter, including field initializers; runs in CI (advisory master, -warnaserror release). Current defaults are compile-time Fixed constants. Verified 2026-07-28.

### DW-218: Determinism-test durability — null-forgiving reflection in TimerDeterminismTests
origin: migrated from legacy ledger ("From code review of story-1.4 (2026-06-23)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Golden/TimerDeterminismTests.cs:47-83
severity: low
reason: Still uses `GetField(...)!`/`GetMethod(...)!` with no null-checked lookup — a rename produces an opaque NRE. The TriggerOrderingTests half is largely obsolete (per-tick Array.Sort replaced by stable OrderBy; the introsort-threshold coupling is now only a historical guard). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle tier1-test-hardening (Epic 15, Story 15.5)

## From code review of story-2.9b (2026-07-02) — migrated 2026-07-28

### DW-219: Negative unit cost_crystal grants crystal instead of costing it
origin: migrated from legacy ledger ("From code review of story-2.9b (2026-07-02)"), 2026-07-28
location: godot/src/Core/Definitions/UnitDefinitionValidator.cs:343-350
severity: high
reason: TrainUnit had no lower-bound guard on authored costs.
status: done 2026-07-28
resolution: Epic 4/5 validators — UnitDefinitionValidator.CheckCost rejects v < 0 (message names the exploit) and v ≥ 32768, for cost_ore/cost_crystal and the 4.3 sparse cost map; whole-faction ResourceCostValidator wired at FactionValidator.cs:125; spend path is now check-all-then-spend-all. Verified 2026-07-28.

### DW-220: start_crystal / start_ore had no upper bound (16.16 overflow ≥ 32768)
origin: migrated from legacy ledger ("From code review of story-2.9b (2026-07-02)"), 2026-07-28
location: godot/src/Core/Definitions/ScenarioValidator.cs:1313-1320
severity: medium
reason: ScenarioApplier converted via FromFloat with no ceiling.
status: done 2026-07-28
resolution: ScenarioValidator.CheckNonNeg now runs InRange(v) against Range=32768 before the ≥0 check; both start-resource fields plus supply/rate/revival/starting_amount inherit the gate. Verified 2026-07-28.

### DW-221: matter_infusion's apply_modifier/move_speed_delta path never exercised by a worker-cast test
origin: migrated from legacy ledger ("From code review of story-2.9b (2026-07-02)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Effects/WorkerCastTests.cs:20-27
severity: low
reason: Worker-cast tests still use a SelfHeal with matter_infusion-shaped costs; no test applies a move-speed modifier to a mid-gather worker and asserts gather-state + checksum determinism. Coverage hardening, not a known defect. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle tier1-test-hardening (Epic 15, Story 15.5)

### DW-222: Fallback crystal seed literal duplicated in 3 comment-coupled places
origin: migrated from legacy ledger ("From code review of story-2.9b (2026-07-02)"), 2026-07-28
location: godot/src/Core/Sim/ScenarioApplier.cs:406-416
severity: low
reason: ApplyFallback/BuildFallbackMirror/alpha_map_01.json each hardcoded 100.
status: done 2026-07-28
resolution: Story 7.7 retired the legacy un-tokened writer — ONE code-side fallback model remains (ScenarioApplier.BuildFallbackMirror), consumed by ScenarioLoadPhase + MainScene and pinned by FallbackMirrorParityTests. Residual (accepted): still comment-coupled to alpha_map_01.json's start_crystal — agreement-test idea recorded in DW-324's housekeeping sweep. Verified 2026-07-28.

### DW-223: Command-card ability/worker panel positions cached once — stale on window resize
origin: migrated from legacy ledger ("From code review of story-2.9b (2026-07-02)"), 2026-07-28
location: godot/src/UI/CommandCardSystem.cs:1294-1295 (+ sibling compute-once sites :949, :1097, :1171)
severity: low
reason: _abilityPanelNormalPos/_abilityPanelStackedPos still computed once from vpSize in BuildAbilityPanel; no viewport-resize subscription anywhere in the file. Presentation-only. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle hud-viewport-resize (Epic 15, Story 15.8)

## From code review of story-1.5 (2026-06-23) — migrated 2026-07-28

### DW-224: No replay test records real orders through a system that draws from world.Rng
origin: migrated from legacy ledger ("From code review of story-1.5 (2026-06-23)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs:108-145
severity: low
reason: The v4 successor test still records zero orders with a synthetic RNG loop. Order round-tripping (V4MultiTickOrders) and production RNG draws (random_choice via ScenarioDirector/DSL) are now separately covered, but no single test replays recorded orders through an Rng-drawing system. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle tier1-test-hardening (Epic 15, Story 15.5)

### DW-225: ReplayRecorder hardcodes DEFAULT_RNG_SEED — no per-match seed producer exists
origin: migrated from legacy ledger ("From code review of story-1.5 (2026-06-23)"), 2026-07-28
location: godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs:204
severity: medium
reason: Still constructs `new ReplayRecorder(..., EntityWorld.DEFAULT_RNG_SEED, ...)`. Epic 9 shipped the FORMAT side (v4 header carries a seed; ReplayPlayer reseeds world.Rng) but no per-match seed producer exists (SimulationHost.cs:188-190 explicitly defers it — "would move the golden"). Correct today, silently wrong the day a real match seed lands. Sibling half of DW-17 (in-place reset reseed) — must ship together. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle match-seed-plumbing with DW-17 (Epic 15, Story 15.5)

### DW-226: ulong.MaxValue seed never pinned as a SimRng stream
origin: migrated from legacy ledger ("From code review of story-1.5 (2026-06-23)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Determinism/SimRngTests.cs
severity: low
reason: Pinned streams exist only for seeds 0 and 12345; the max-seed boundary appears only as a wrong-seed fixture. ~5-line externally-computed assertion. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle tier1-test-hardening (Epic 15, Story 15.5)

## From code review of story-1.6 (2026-06-23) — migrated 2026-07-28

### DW-227: DamageTable.FromJson silently last-wins on duplicate JSON keys
origin: migrated from legacy ledger ("From code review of story-1.6 (2026-06-23)"), 2026-07-28
location: godot/src/Combat/DamageTable.cs:98-149
severity: medium
reason: Unchanged — nested-dictionary Dto with only weak dimension-count checks; no JsonDocument/Utf8JsonReader raw-property-count pass, so a creator's duplicated row/cell key silently drops the earlier value (verified STJ behavior). Future-content-only (shipped table clean). Other dictionary-shaped loaders may share the class. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle loader-duplicate-key-fail-closed (Epic 15, Story 15.6)

## From code review of story-1.7 (2026-06-23) — migrated 2026-07-28

### DW-228: default(Validated<T>) could mint a fake validated value (re-raised on 1.8b)
origin: migrated from legacy ledger ("From code review of story-1.7 (2026-06-23)"), 2026-07-28
location: godot/src/Core/Sim/ScenarioApplier.cs:129-141
severity: high
reason: A default struct bypassed the sole-minter Proof guarantee at the 1.8b consumption point.
status: done 2026-07-28
resolution: ScenarioApplier.Apply now guards at the consumption point before any store write (null-model reject, comment credits this deferral); a follow-up moved RevivalRuntime.Configure/ConfigureSupply below the guard; Story 7.7 deleted the failure-carrying overload and shadow mode. Verified 2026-07-28.

## Deferred from: code review of story-1.8b (2026-06-24) — migrated 2026-07-28

### DW-229: ResolveSlotFactionDefs never resets entries — and Edit↔Play never re-runs it
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8b (2026-06-24)"), 2026-07-28
location: godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:345-379; godot/src/Core/MainScene.cs:1930,2021-2041
severity: high
reason: ESCALATED from latent: still `continue`s on empty FactionJson with no per-apply reset (Snapshot/RestoreSlotFactionDefs only rolls back on validator REJECT), and the "single-apply path" premise is now false — MainScene.ResetToAuthoredStart (the 3.10 Edit↔Play loop) re-applies against the same _slotFactionDefs array and NEVER calls ResolveSlotFactionDefs. In-session faction_json changes (LLMService.cs:551, MapGeneratorPhase.cs:36-39) silently don't apply until a full scene reload; a cleared faction_json keeps the stale def. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle scenario-reapply-slot-faction-def-refresh, absorbing bundle editor-panel-scenario-rebind (DW-10) (Epic 15, Story 15.6)

### DW-230: Scenario with > 64 resource nodes / > 64 buildings silently drops the overflow
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8b (2026-06-24)"), 2026-07-28
location: godot/src/Core/Sim/ScenarioApplier.cs:221,254-256
severity: medium
reason: Unchanged — Nodes.Create's -1 discarded, PlaceBuildingDirect/ById -1 assigned unchecked into buildingSlots; validator has no count cap. Slightly worse than logged: the -1 now flows into Story 7.11's landmark structure_index mapping, which the validator bounds only against authored buildings.Length, not successful placement. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle scenario-store-capacity-fail-closed (Epic 15, Story 15.6)

## Deferred from: code review of story-1.8c (2026-06-24) — migrated 2026-07-28

### DW-231: ResolveSlotFactionDefs IndexOutOfRange for player slots 4-7
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8c (2026-06-24)"), 2026-07-28
location: godot/src/Core/FactionRegistry.cs:36
severity: high
reason: The [5]-sized slot array crashed for slots 4-7. Same family as DW-96.
status: done 2026-07-28
resolution: Story 9.2 (=DW-96) — SLOT_DEFINITIONS_SIZE = FACTION_ARRAY_SIZE = 9; MainScene seeds from factions.SlotDefinitions. Narrow residual noted for next touch: ScenarioLoadPhase.cs:376 lacks the bounds one-liner its server-side mirror has (MainScene.cs:2269) for authored slots ≥ 8 ahead of the validator reject. Verified 2026-07-28.

### DW-232: SceneContext null! cross-phase coupling is load-bearing and unguarded (40 phases; already bit once)
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8c (2026-06-24)"), 2026-07-28
location: godot/src/Core/Bootstrap/Phases/SceneContext.cs
severity: high
reason: ESCALATED: 68 `null!` fields, zero guards, phase count grew 22→40, and the predicted failure fired in Epic 9 (CustomHudOverlayPhase captured SceneContext.Lockstep by value 13 phases early → permanently-null handle, silent no-op, invisible to all Tier-1 tests; fixed with a late-bound getter but no structural guard added). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle scenecontext-producer-consumer-guards (Epic 15, Story 15.7): late-bound getters or a Required<T> accessor throwing with the producing phase's name

### DW-233: PhaseOrderTest cannot catch concrete-phase Name drift or duplicate canonical names
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8c (2026-06-24)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs
severity: low
reason: Unchanged — no duplicate-name assert (≈3 lines, Tier-1) and no Tier-2 GdUnit4 companion instantiating the real phases; value rose with 40 phases. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — rides bundle scenecontext-producer-consumer-guards (Epic 15, Story 15.7)

### DW-234: BuildFallbackMirror vs ApplyFallback duplication (two fallback sources of truth)
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8c (2026-06-24)"), 2026-07-28
location: godot/src/Core/Sim/ScenarioApplier.cs:395-416
severity: medium
reason: Two code paths authored the default scenario.
status: done 2026-07-28
resolution: Story 7.7 retired the legacy un-tokened ApplyFallback writer; single source BuildFallbackMirror pinned by FallbackMirrorParityTests. Stale text anchors noted in DW-324 housekeeping. Verified 2026-07-28.

### DW-235: ContentBrowserPhase vs WinConditionPhase .chimera.zip import paths — drifted: browser loads terrain maps FLAT
origin: migrated from legacy ledger ("Deferred from: code review of story-1.8c (2026-06-24)"), 2026-07-28
location: godot/src/Core/Bootstrap/Phases/ContentBrowserPhase.cs:51-95; WinConditionPhase.cs:556-620
severity: high
reason: ESCALATED — the predicted drift happened: DoImport gained Story 6.2's TerrainFiles handling (copy + TerrainRef rewrite) and HandleLoadMap did NOT, so a terrain-bearing .chimera.zip loaded through the Content Browser silently loads flat with a stale TerrainRef. Distinct from DW-145 (zero-terrain else-branch), the Epic-14 ungated-SaveToFile bullet, and the Epic-9 stale imported_maps/<id> bullet — one shared ImportMapPackage helper closes all legs. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle map-package-import-one-path, absorbing bundle content-package-import-roundtrip (DW-82, DW-145, DW-156) (Epic 15, Story 15.6)

## Deferred from: code review of story-1.9a (2026-06-24) — migrated 2026-07-28

### DW-236: Quorum peer-set fixed at construction — mid-match disconnect leg
origin: migrated from legacy ledger ("Deferred from: code review of story-1.9a (2026-06-24)"), 2026-07-28
location: godot/src/Multiplayer/Server/ServerChecksumCollector.cs:164-206
severity: critical
reason: After any mid-match drop the server's desync guard went silently dead.
status: done 2026-07-28
resolution: Story 9.6 — _expected is mutable with DropExpectedReporter (slot exclusion, floor 1, in-flight bucket re-tally); ServerHost.DropReporter routes through shared ProcessVerdict; DedicatedServer calls it on DropAck commit and HandleDisconnect no longer ends the match. Known 9.6 follow-ons remain separately ledgered. Verified 2026-07-28.

### DW-237: N≥3 minority desync self-halt silently kills the surviving majority's desync guard
origin: migrated from legacy ledger ("Deferred from: code review of story-1.9a (2026-06-24)"), 2026-07-28
location: godot/src/Multiplayer/Server/ServerHost.cs:95-103; godot/src/Multiplayer/LockstepManager.cs:372,482-486
severity: critical
reason: ESCALATED from unreachable (N=2) to live (9.7/9.15 shipped 4-player MP: MpSeatCeiling=4). ProcessVerdict still sends DesyncAlert without Halted; the alerted minority halts, stops sending checksums while staying CONNECTED, so DropReporter never runs, _expected still counts it, no bucket completes → survivors' guard silently dead. Self-heals only if the human clicks the halt overlay and disconnects. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle minority-halt-quorum-rebase (Epic 15, Story 15.7): drop an alerted minority from the quorum or force-disconnect/HALT it; pairs with the ledgered "drop-path HALT branch untested" 9.6 bullet. Doc rot: ServerChecksumCollector.cs:11,22-23 stale "MaxSlots=4 → 8 in 9.2" comment (9.2 correctly did NOT bump it; sim ceiling 8 vs MP seat ceiling 4 are deliberately different) → DW-324

## Deferred from: code review of story-1.9b (2026-06-24) — migrated 2026-07-28

### DW-238: LAN launcher's server/client/F9 triggers are #if DEBUG — no-ops in an exported build
origin: migrated from legacy ledger ("Deferred from: code review of story-1.9b (2026-06-24)"), 2026-07-28
location: godot/src/Core/MainScene.cs:262-265,584-600,658-675; godot/tools/lan-desync-smoke.ps1
severity: medium
reason: Unchanged and still correctly parked: --server/--autojoin/F9 remain DEBUG-gated; the non-DEBUG server trigger (headless / dedicated_server feature) exists at MainScene.cs:261. Latent until Epic 10 exports (10.5/10.8). Cheap interim: a "requires a source/DEBUG build" banner in the script header. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed to Stories 10.5/10.8 (Epic 10); banner one-liner rides DW-324 housekeeping

### DW-239: Collector window: a valid-but-late checksum 8 ticks behind is silently abandoned
origin: migrated from legacy ledger ("Deferred from: code review of story-1.9b (2026-06-24)"), 2026-07-28
location: godot/src/Multiplayer/Server/ServerChecksumCollector.cs:124-128
severity: medium
reason: Code byte-identical — Reset of an older incomplete bucket on ring overrun; _resolvedThrough never advances past the abandoned tick so it never yields a verdict. Story 9.4's adaptive delay makes a far-behind peer MORE plausible than the original "≤2 in flight" premise. Cheap fix: advance _resolvedThrough to the evicted tick and log it as abandoned. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle minority-halt-quorum-rebase (Epic 15, Story 15.7)

## Deferred from: code review of story 1.11 (2026-06-25) — migrated 2026-07-28

### DW-240: spawn_unit.unit_id (and pre-placed units) not validated by the scenario gate
origin: migrated from legacy ledger ("Deferred from: code review of story 1.11 (2026-06-25)"), 2026-07-28
location: godot/src/Core/Definitions/ScenarioValidator.cs:340-355,763-782
severity: medium
reason: Still no known-unit_id check in either the trigger spawn_unit action pass or the pre-placed units loop. Severity reduced since logged: the runtime now fails closed-and-loud (ScenarioDelegateBinder.cs:34-39 resolves the def, warns + returns on null) — the residual gap is no load-time reject / no editor feedback. The pre-placed half may need a fail-closed decision for existing scenarios. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle scenario-unit-id-validation (Epic 15, Story 15.6)

## Deferred from: code review of story-1.12 (2026-06-25) — migrated 2026-07-28

### DW-241: Patrol/Follow request no navigation path, unlike Move/AttackMove
origin: migrated from legacy ledger ("Deferred from: code review of story-1.12 (2026-06-25)"), 2026-07-28
location: godot/src/Multiplayer/NetworkCommand.cs:388-414
severity: low
reason: Unchanged — Follow/Patrol/PatrolAppend invoke neither onRequestPath nor onRequestAttackMove (presentation-only delegates; null on headless/golden/replay). Obstacle-clipping polish, not correctness.
status: done 2026-07-28
resolution: Closed as accepted scope (correct-course 2026-07-28); pathing-quality pointer filed to Story 10.14. Verified 2026-07-28.

### DW-242: Zero-damage units are locked out of Patrol/Follow/AttackBuilding by the pre-switch combat gate
origin: migrated from legacy ledger ("Deferred from: code review of story-1.12 (2026-06-25)" + "Deferred from: code review of story-2.9a (2026-07-01)" item 1 — merged), 2026-07-28
location: godot/src/Combat/CombatSystem.cs:121-126
severity: medium
reason: The `EffectiveAttackDamage == 0 → continue` gate still sits above the whole command switch (which now also carries AttackBuilding/PickupItem/Patrol/Follow), so zero-damage non-gatherers sit inert in those commands with no normalize-to-Idle. Fix = hoist pure-movement branches above the gate TOGETHER WITH DW-202's wave filter (else zero-damage units start auto-attacking). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle noncombatant-command-gate (Epic 15, Story 15.4)

## Deferred from: code review of story-1.13 (2026-06-25) — migrated 2026-07-28

### DW-243: RestoreUnit (editor snapshot) did not carry the 1.13/2.2a per-unit fields
origin: migrated from legacy ledger ("Deferred from: code review of story-1.13 (2026-06-25)" + "Deferred from: story 2.2a (2026-06-25)" item 6 — merged), 2026-07-28
location: godot/src/Core/EntityWorld.cs:1043-1112
severity: high
reason: The editor snapshot dropped def-derived fields (collision_radius, separation_priority, category, Energy/MaxEnergy).
status: done 2026-07-28
resolution: Story 3.17 (=DW-24) — SnapshotUnit captures Def = SourceDefinition[id]; RestoreUnit routes through ApplyUnitDefinition re-deriving every def field, then replays caller-owned residue. Residuals separately tracked: DW-51 (closed), DW-52 (open), DW-53 (closed), DW-54 (open). Verified 2026-07-28.

## Deferred from: story 2.1 (2026-06-25) — D1 Effect-Graph keystone scope carve-offs — migrated 2026-07-28

### DW-244: ApplyModifierEffect / PersistentEffect periodic execution
origin: migrated from legacy ledger ("Deferred from: story 2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/EffectExecutor.cs:113-138
severity: high
reason: Both TYPE kinds existed but did not execute (2.1 guard).
status: done 2026-07-28
resolution: Story 2.2b — EffectExecutor dispatches Persistent→InstallPersistent and ApplyModifier→ModifierStore.Apply; ModifierSystem.Tick drives Advance; DotHotPeriodTests/LifelongHotTests/ModifierStoreApplyTests. Verified 2026-07-28.

### DW-245: Air / Ground / Structure TargetFilter evaluation
origin: migrated from legacy ledger ("Deferred from: story 2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/TargetMatcher.cs:15-47
severity: medium
reason: Bits existed but were not evaluated.
status: done 2026-07-28
resolution: Story 2.9a — TargetMatcher maps DomainClassifier.Of() onto the flag bits with the AND-constraint; shared classifier in AttackDomain.cs; AbilityDomainFilterTests. Verified 2026-07-28.

### DW-246: SetVariable leaf → Trigger DSL home
origin: migrated from legacy ledger ("Deferred from: story 2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Dsl/NodeBase.cs:630
severity: medium
reason: The DSL variable store was always the chartered home.
status: done 2026-07-28
resolution: Epic 7 — set_variable landed as a DSL ActionNode kind (typed/validated ScenarioValidator.cs:750-761,983-1003; executed ScenarioDirector.cs:123); the effect vocabulary correctly stayed at 8 closed kinds. Verified 2026-07-28.

### DW-247: FireProjectile / SpawnUnit / Victory reserved effect leaves
origin: migrated from legacy ledger ("Deferred from: story 2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Dsl/NodeBase.cs:630; godot/src/Effects/EffectNodeJsonConverter.cs:40-60
severity: medium
reason: Reserved leaf vocabulary was unbuilt.
status: done 2026-07-28
resolution: SpawnUnit + Victory landed as DSL actions (MaxSpawnCount enforced at DslLoopGate/ScenarioValidator, folded in RulesetHash); FireProjectile obsolete-as-designed — Story 3.12 modelled projectiles as authored Delivery/ProjectileSpeed on UnitDefinition + ProjectileSystem, no effect leaf needed. Teleport/presentation residue split to DW-248. Verified 2026-07-28.

### DW-248: Teleport + presentation effect leaves (PlayVfx/PlaySound/ShakeScreen) unbuilt
origin: migrated from legacy ledger ("Deferred from: story 2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/
severity: low
reason: Zero occurrences anywhere in src — unbuilt with no owning story in the 10-13 backlog. Feature vocabulary, not a defect. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed as Story 15.13 (effect vocabulary completion), per Alec's fold-into-Epic-15 decision

### DW-249: SearchArea over-cap selection is cell-ordered, not global lowest-ID
origin: migrated from legacy ledger ("Deferred from: story 2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/SearchAreaEffect.cs:51-64; godot/src/Navigation/SpatialHash.cs:179
severity: medium
reason: Unchanged — QueryRadius has no filter/priority parameter; the caveat comment survives verbatim. Deterministic but not the documented contract. One SpatialHash API change closes this + DW-250. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle searcharea-target-selection-correctness (Epic 15, Story 15.4)

## Deferred from: code review of story-2.1 (2026-06-25) — migrated 2026-07-28

### DW-250: SearchArea truncates the hit buffer BEFORE the faction filter — enemy fan-out under-selects
origin: migrated from legacy ledger ("Deferred from: code review of story-2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/SearchAreaEffect.cs:64-78
severity: medium
reason: Unchanged — 64-slot buffer fills unfiltered, sorts, THEN compacts by TargetMatcher; the recommended pass-filter-into-QueryRadius fix has not happened. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle searcharea-target-selection-correctness (Epic 15, Story 15.4)

### DW-251: No total-work / total-node-count bound on an effect graph
origin: migrated from legacy ledger ("Deferred from: code review of story-2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/EffectCaps.cs:81-89
severity: high
reason: EffectBounds capped depth but not total work.
status: done 2026-07-28
resolution: Story 2.3 — MaxTotalEffectNodes=64 + MaxSearchAreaDepth=2 enforced in AbilityValidator, folded into RulesetHash; NegativeAbilityValidationTests teeth (65-node reject). Verified 2026-07-28.

### DW-252: Zero-alloc executor test misses the lethal and 64-wide fan-out paths
origin: migrated from legacy ledger ("Deferred from: code review of story-2.1 (2026-06-25)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Effects/EffectExecutorBoundsTests.cs:184-215
severity: low
reason: Byte-for-byte the flagged shape (8 high-HP enemies, no events queue): neither the UnitKilled-enqueue path nor a 64-wide fan-out is under the allocation delta. Natural regression net for the DW-249/250 fix. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — rides bundle searcharea-target-selection-correctness (Epic 15, Story 15.4)

### DW-253: Load-time Validate admitted the deferred Persistent/ApplyModifier node kinds
origin: migrated from legacy ledger ("Deferred from: code review of story-2.1 (2026-06-25)"), 2026-07-28
location: godot/src/Effects/EffectNodeJsonConverter.cs:40-60
severity: low
reason: Premise expired — both kinds now execute.
status: done 2026-07-28
resolution: Obsolete — discharged by 2.2b execution + 2.3's closed JSON registry and AC5 shape rules (already recorded in the 2.3 carve-off notes). Verified 2026-07-28.

## Deferred from: story 2.2a (2026-06-25) — D1 effective-stat pipeline scope carve-offs — migrated 2026-07-28

### DW-254: The single SimChecksum fold for the effective-stat pipeline
origin: migrated from legacy ledger ("Deferred from: story 2.2a (2026-06-25)"), 2026-07-28
location: godot/src/Core/SimChecksum.cs:330-346
severity: high
reason: The one intentional re-baseline for Effective*/Energy/StatusFlags.
status: done 2026-07-28
resolution: Story 2.2b — per-entity fold of EffectiveAttackDamage/EffectiveMaxHealth/EffectiveMoveSpeed/Energy/StatusFlagsOf + per-slot ModifierStore state; v6 re-baseline documented at :92-98 (AlgoVersion since advanced to 21); ModifierSystem private accumulators correctly unhashed. Verified 2026-07-28.

### DW-255: MaxHealth-buff Health semantics
origin: migrated from legacy ledger ("Deferred from: story 2.2a (2026-06-25)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:453-472
severity: medium
reason: Open design decision: heal-up on apply vs ratio-preserve.
status: done 2026-07-28
resolution: Decided + implemented — ApplyStatDeltas heals up only on positive maxHealthChange with isApply:true, always re-clamps into [0, EffectiveMaxHealth]; rationale documented in-code (kills the wearing-off-debuff net-heal exploit). Verified 2026-07-28.

### DW-256: Move-speed modifiers take effect one tick late (Modifier ordered after Movement)
origin: migrated from legacy ledger ("Deferred from: story 2.2a (2026-06-25)" item 3 + "Deferred from: story 2.2b (2026-06-26)" item 8 — merged), 2026-07-28
location: godot/src/Core/Sim/SimulationHost.cs:268,281
severity: low
reason: MovementSystem @3 vs ModifierSystem @6 — a MoveSpeedDelta applied on tick T is read at T+1. The AR-9 contract (Modifier immediately before Combat) takes precedence; order pinned by SystemOrderTest. Deterministic.
status: done 2026-07-28
resolution: Closed as accepted-by-design (correct-course 2026-07-28); revisit only if a same-tick speed buff is ever specced.

### DW-257: Authored MaxEnergy on UnitDefinition
origin: migrated from legacy ledger ("Deferred from: story 2.2a (2026-06-25)" item 4 + "Deferred from: story 2.2b (2026-06-26)" item 3 — merged), 2026-07-28
location: godot/src/Core/Definitions/UnitDefinition.cs:313-316; godot/src/Core/EntityWorld.cs:1001-1002
severity: medium
reason: Energy SoA existed with no authored source.
status: done 2026-07-28
resolution: Story 2.4a — max_energy on UnitDefinition, written through the single mapper (starts full, Decision #5 no separate starting-energy field); validator/writer/editor/ContentHash coverage; A2 guard teeth (ApplyUnitDefinitionGuardTest:179-224). Verified 2026-07-28.

### DW-258: ModifierStore must clear an entity's accumulators + dirty on death/recycle
origin: migrated from legacy ledger ("Deferred from: story 2.2a (2026-06-25)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:103,339-351; godot/src/Effects/ModifierSystem.cs:109-118
severity: critical
reason: SoA-recycle trap for modifier state.
status: done 2026-07-28
resolution: world.OnDestroy += ClearEntity cascades to ModifierSystem.ClearEntity (zeroes all four _flat*Bonus + _dirty) before the id returns to the free list; ModifierRecycleGuardTest with inject→observe→revert teeth. Verified 2026-07-28.

## Deferred from: code review of story-2.2a (2026-06-26) — migrated 2026-07-28

### DW-259: ModifierSystem recycle cleanup must extend the A2 guard
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2a (2026-06-26)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Effects/ModifierRecycleGuardTest.cs
severity: medium
reason: Both halves of the ask.
status: done 2026-07-28
resolution: Landed as a dedicated recycle guard (rather than an ApplyUnitDefinitionGuardTest extension): asserts a recycled slot carries no residual bonus; documented inject→observe→revert teeth. Intent met. Verified 2026-07-28.

### DW-260: Effective-stat recompute lower-clamp / overflow guard
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2a (2026-06-26)"), 2026-07-28
location: godot/src/Effects/ModifierSystem.cs:88-93,150-153
severity: medium
reason: Split verdict — lower-clamp landed; overflow half remains the DW-28 class.
status: done 2026-07-28
resolution: Lower-clamp shipped in 2.2b (Fixed.Max(Zero, Base+Σ) on all four stats, "cannot attack ⇒ Disarmed, never sub-zero" policy documented; MaxHealth clamp inversion closed). The unsaturated AccumulateBonus overflow half is exactly DW-28 (open) — seen-again noted there, no duplicate entry. Verified 2026-07-28.

### DW-261: EntityPlacer snapshot captured Effective and restored it into Base
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2a (2026-06-26)"), 2026-07-28
location: godot/src/Core/EntityWorld.cs:1046-1052,1057,1092-1097
severity: medium
reason: Live-modifier deltas would bake into Base and double-count on passive re-install.
status: done 2026-07-28
resolution: Story 3.17 — SnapshotUnit captures BaseMaxHealth/BaseMoveSpeed with an explicit comment naming this defect. Residual (low, def-less editor units only, carry no modifiers today): the def-less branch still captures EffectiveAttackDamage into both Base and Effective — noted alongside DW-54's snapshot cluster. Verified 2026-07-28.

### DW-262: A2 guard coverage is hand-maintained
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2a (2026-06-26)"), 2026-07-28
location: godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs; godot/CLAUDE.md
severity: low
reason: Still true, now codified as a standing convention rather than fixed — godot/CLAUDE.md states the round-trip guard is a hand-enumerated regression net and the written rule IS the coverage for new residue fields.
status: done 2026-07-28
resolution: Closed as accepted convention (correct-course 2026-07-28) — not actionable as a story.

## Deferred from: story 2.2b (2026-06-26) — migrated 2026-07-28

### DW-263: SearchArea inside a period effect
origin: migrated from legacy ledger ("Deferred from: story 2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Core/Definitions/AbilityValidator.cs:154-196
severity: medium
reason: Period effects were direct-target-only at runtime.
status: done 2026-07-28
resolution: Closed via the named validator branch — AbilityValidator rejects SearchArea in any Persistent phase and descends modifier.period_effect, so the direct-target-only executor can never receive a search-bearing period. Aura/AoE-DoT capability remains an unbuilt feature (not this defect). Verified 2026-07-28.

### DW-264: Independent per-stack expiry timers (StackRule)
origin: migrated from legacy ledger ("Deferred from: story 2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:151-176
severity: low
reason: Single slot + shared duration unchanged; _stackCount scales stat deltas only. No shipped mechanic wants independent decay. Design carve-off, not a defect. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed as Story 15.12 (energy & stack mechanics), per Alec's fold-into-Epic-15 decision

### DW-265: No energy regen model exists
origin: migrated from legacy ledger ("Deferred from: story 2.2b (2026-06-26)" item 4 + "Deferred from: story 2.4a (2026-06-27)" item 3 + "Deferred from: story 2.4b (2026-06-28)" item 3 — merged), 2026-07-28
location: godot/src/Core/EntityWorld.cs:998; godot/src/Effects/ModifierStore.cs:386-396
severity: medium
reason: Only Energy writers are the cast debit and start-full apply; zero regen_rate/EnergySystem hits in src or resources/data. Casters are one-tank-per-life. Behavioral change when built (Energy already folded → goldens move). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed as Story 15.12 (energy & stack mechanics), per Alec's fold-into-Epic-15 decision

### DW-266: StatusFlags are authorable and hashed but read by NOTHING — authored stuns are silent no-ops
origin: migrated from legacy ledger ("Deferred from: story 2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Combat/CombatSystem.cs; godot/src/Navigation/MovementSystem.cs; godot/src/Effects/AbilityCastSystem.cs
severity: critical
reason: HEADLINE GAP — StatusFlagsOf is written/folded (EntityWorld, ModifierStore, SimChecksum) but whole-repo grep shows zero reads in CombatSystem/MovementSystem/AbilityCastSystem, while all five flags (Disarmed/Stunned/Rooted/Silenced/Invulnerable) are authorable through the 2.5 editor and JSON. An authored stun costs energy, shows in the HUD, and does nothing. Determinism-relevant fix (golden re-baseline). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — Story 15.3 (status effects become real): Disarmed→CombatSystem gate, Rooted/Stunned→MovementSystem+combat, Silenced→AbilityCastSystem refuse, Invulnerable→DamageResolver

### DW-267: Lethal period DamageEffect mid-Advance is unexercised by any test
origin: migrated from legacy ledger ("Deferred from: story 2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:252-255,314
severity: low
reason: Defensive machinery looks correct on inspection (alive-check breaks after a pulse; caster attribution via _casterFaction) but no shipped content authors a lethal period and SelfLethalCastTests covers cast-time only. Verification gap, not a known defect. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — test rides Story 15.3 (status effects become real)

### DW-268: EntityPlacer snapshot of Effective/modifier state (2.2b facet)
origin: migrated from legacy ledger ("Deferred from: story 2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Core/EntityWorld.cs:1043-1097
severity: medium
reason: Same surface as DW-261/DW-243.
status: done 2026-07-28
resolution: Story 3.17 closed the dangerous bake-the-buff half; transient modifier instances are dropped by design (Destroy→ClearEntity), def-derived passives re-install via the mapper. Def-less residual noted at DW-261. Verified 2026-07-28.

## Deferred from: code review of story-2.2b (2026-06-26) — migrated 2026-07-28

### DW-269: Runtime re-entrancy guard / deferred-application queue for ModifierStore
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Core/Definitions/AbilityValidator.cs:154-181
severity: medium
reason: A period graph that installs modifiers could mutate the store mid-Advance.
status: done 2026-07-28
resolution: Fenced at the load-time gate (2.3): ApplyModifier rejected in any Persistent phase, nested Persistent rejected, rules extended into modifier.period_effect. Runtime _running flag remains unbuilt = defense-in-depth only (non-ability installers all call Apply outside Advance); noted in DW-324's doc sweep (ModifierStore.cs:39 stale comment). Verified 2026-07-28.

### DW-270: Modifier.DurationTicks == 0 is not truly instantaneous (doc-vs-code mismatch)
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:151,281-284; godot/src/Effects/Modifier.cs:48
severity: low
reason: Unchanged — 0 stored verbatim, decrements to -1 then expires, so the bonus is live for one full tick while the doc says "instantaneous/one-shot". Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle modifier-period-semantics-and-authoring-warnings (Epic 15, Story 15.3)

### DW-271: Periodic Modifier truncates at 256 pulses while still active (Modifier path — NOT closed by 2.13)
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:270-275,532
severity: medium
reason: 2.13's lifelong re-arm is gated on `_persistent[slot] != null && Lifelong` — a Modifier's schedule is still hard-set to MaxPersistentPeriods with no re-arm and no duration coupling: periodTicks=1 + duration>256 goes silently pulse-less while keeping its stat bonus. (The 2.6-review persistent self-passive sibling IS closed by 2.13 — do not conflate.) Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle modifier-period-semantics-and-authoring-warnings (Epic 15, Story 15.3)

### DW-272: Stacked periodic Modifier doesn't scale its DoT/HoT per stack
origin: migrated from legacy ledger ("Deferred from: code review of story-2.2b (2026-06-26)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:320-324,536-541
severity: low
reason: Unchanged — RunEffect executes the period graph once per slot with no _stackCount term while RemoveSlot multiplies stat deltas by it; a 3-stack DoT ticks 1×. Design decision + validator warning first; behavior change waits for stacking content. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — warning half in bundle modifier-period-semantics-and-authoring-warnings (Story 15.3); behavior half rides Story 15.12

## Deferred from: story 2.3 (2026-06-26) — migrated 2026-07-28

### DW-273: EffectNode converter Write / authoring round-trip
origin: migrated from legacy ledger ("Deferred from: story 2.3 (2026-06-26)"), 2026-07-28
location: godot/src/Effects/EffectNodeJsonConverter.cs:60-74
severity: high
reason: 2.3 validated the model; nothing wrote it back.
status: done 2026-07-28
resolution: Story 2.5a — Write→WriteNode with documented exact-inverse-of-Read contract; pinned by AbilityRoundTripTests/AbilityDraftTests/AbilityPresetTests; editor saves via ContentJson.Options. Verified 2026-07-28.

### DW-274: Migrate ScenarioSerializer / FactionDefinition to ContentJson.Options
origin: migrated from legacy ledger ("Deferred from: story 2.3 (2026-06-26)"), 2026-07-28
location: godot/src/Core/ScenarioSerializer.cs:31; godot/src/Core/Definitions/FactionDefinition.cs:191
severity: low
reason: Unchanged — both still carry private option sets while ContentJson.Options spread to items/DSL/LLM drafts. The D3 loader-unification residue; the open EOL/hash exposure recorded in the Epic-6 section touches the same ScenarioSerializer surface — do together. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — rides Story 15.6 (scenario & content pipeline sweep)

### DW-275: Runtime ability-cast path
origin: migrated from legacy ledger ("Deferred from: story 2.3 (2026-06-26)"), 2026-07-28
location: godot/src/Core/Sim/SimulationHost.cs:274-277
severity: high
reason: 2.3 validated; nothing cast.
status: done 2026-07-28
resolution: Stories 2.4a/2.4b — AbilityCastSystem at pinned index 5, per-slot cooldown ring, command-card/HUD wiring. Verified 2026-07-28.

### DW-276: Fold the structural caps into the Epic-9 rulesetHash
origin: migrated from legacy ledger ("Deferred from: story 2.3 (2026-06-26)"), 2026-07-28
location: godot/src/Core/Definitions/RulesetHash.cs:39-48
severity: high
reason: Caps had to be peer-agreed.
status: done 2026-07-28
resolution: Story 9.4 — full EffectCaps set folded (incl. MaxSearchAreaDepth/MaxTotalEffectNodes); consumed by MatchAgreementHash, replay header, match-start gate. Stale "reserved to fold" comments at EffectCaps.cs:8/79/87 → DW-324. Verified 2026-07-28.

### DW-277: SearchArea/large-graph structural caps (closed in-review 2026-06-26)
origin: migrated from legacy ledger ("Deferred from: story 2.3 (2026-06-26)" item 5, already marked ✅ CLOSED), 2026-07-28
location: godot/src/Core/Definitions/AbilityValidator.cs
severity: medium
reason: Was closed by the 2.3 code review itself; carried here for zero-legacy completeness.
status: done 2026-06-26
resolution: Closed by code-review of story 2.3 (original marker preserved); AC5 shape rules + caps verified again 2026-07-28.

### DW-278: Ability validator has no WARNING channel for authorable-but-inert content
origin: migrated from legacy ledger ("Deferred from: story 2.3 (2026-06-26)" item 6 + residuals of the 2.5b review items 1-2 — merged), 2026-07-28
location: godot/src/Core/Definitions/AbilityValidationResult.cs; godot/src/Core/Definitions/AbilityValidator.cs
severity: medium
reason: AbilityValidationResult still carries only Ok/Error/Value — zero non-fatal diagnostics anywhere in the validator, so the 2.2b-class footguns (DurationTicks=0 semantics, >256-pulse truncation, non-scaling stacked DoT, all-empty Persistent on an ACTIVE ability) remain authorable-and-silent. The 2.6 hard rules cover passives only. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle modifier-period-semantics-and-authoring-warnings (Epic 15, Story 15.3): add a Warnings list + surface in the 2.5 editor status line

## Deferred from: story 2.4a (2026-06-27) — migrated 2026-07-28

### DW-279: Command-card UI + in-game cast wiring
origin: migrated from legacy ledger ("Deferred from: story 2.4a (2026-06-27)"), 2026-07-28
location: godot/src/UI/CommandCardSystem.cs:1330-1432
severity: high
reason: 2.4a was the sim spine only.
status: done 2026-07-28
resolution: Story 2.4b — ability card render + press handler, SelectionSystem.ArmCastTargeting/IssueCastAbilityCommand, registry via AbilityRegistry.LoadFromDirectory, server parity in ServerBootstrap, HUD energy; AbilityWiringTeethTest. Verified 2026-07-28.

### DW-280: GroundPoint-targeted casting unbuilt (wire widen + EffectContext ground field + reticle)
origin: migrated from legacy ledger ("Deferred from: story 2.4a (2026-06-27)" item 2 + "Deferred from: story 2.4b (2026-06-28)" item 1 — merged), 2026-07-28
location: godot/src/Multiplayer/NetworkCommand.cs:154,462-466; godot/src/Effects/EffectContext.cs
severity: medium
reason: Unchanged — UnitOrder.SIZE=11, CastAbility packs slot/targetId only, EffectContext has no ground-point field, card renders "[ground-cast: coming soon]" disabled. Needs wire 11→12 + ReplayRecorder.VERSION bump + validator/golden re-baseline + reticle. Carry DW-290 (disable-gate/press-handler coupling) as an AC: fold both targeting sets into one shared is-castable predicate when the 5th mode lands. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed as Story 15.11 (ability targeting increments), per Alec's fold-into-Epic-15 decision

### DW-281: RestoreUnit ability restore (editor snapshot)
origin: migrated from legacy ledger ("Deferred from: story 2.4a (2026-06-27)"), 2026-07-28
location: godot/src/Core/EntityWorld.cs:1043-1090
severity: medium
reason: Snapshot did not carry abilities/MaxEnergy.
status: done 2026-07-28
resolution: Story 3.17 — snapshot carries Def, RestoreUnit re-derives abilities/MaxEnergy through the single mapper; residuals tracked as DW-51/53/54. Verified 2026-07-28.

### DW-282: MAX_ABILITIES_PER_UNIT (=4) raise + silent cap-drop
origin: migrated from legacy ledger ("Deferred from: story 2.4a (2026-06-27)"), 2026-07-28
location: godot/src/Core/EntityWorld.cs:141; godot/src/Core/Definitions/UnitDefinition.cs:387-407
severity: low
reason: Const still 4; content max is 2 per unit and passives now take dedicated slots (headroom grew). The silent-clamp logging half is DW-285's scope.
status: done 2026-07-28
resolution: Closed as obsolete-until-content-demands (correct-course 2026-07-28); raise the const when a roster actually approaches it.

## From code review of story-2.4a (2026-06-28) — migrated 2026-07-28

### DW-283: Ability cost debits discard return values — negative CostEnergy would partial-spend
origin: migrated from legacy ledger ("From code review of story-2.4a (2026-06-28)"), 2026-07-28
location: godot/src/Effects/AbilityCastSystem.cs:193,215-217,240-252
severity: medium
reason: Unchanged, and the check-then-debit surface GREW: a 4th cost (CostHealth, debited after the graph) now relies on the same pre-check/debit lockstep; TryDebitEnergy returns false without mutating on cost<0 while the pre-check passes negatives. Gated only by the authoring validator today. Cheap fail-closed asserts on the debit returns. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle ability-cast-path-hardening (Epic 15, Story 15.9)

### DW-284: SecondsToTicks 16.16 overflow for cooldowns above ~1092s — no validator upper bound
origin: migrated from legacy ledger ("From code review of story-2.4a (2026-06-28)"), 2026-07-28
location: godot/src/Effects/AbilityCastSystem.cs:81,255; godot/src/Core/Definitions/AbilityValidator.cs:81-82
severity: low
reason: Unchanged — conversion unclamped, validator has only the lower bound. ScenarioDirector guards its own conversion with Math.Max(1, ...) — precedent for the one-line fix + validator upper bound. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle ability-cast-path-hardening (Epic 15, Story 15.9)

### DW-285: Ability-resolution and cast failures degrade silently (link-time drops + runtime bare returns)
origin: migrated from legacy ledger ("From code review of story-2.4a (2026-06-28)" item 3 + "Deferred from: story 2.4b (2026-06-28)" item 5 runtime half + the DW-282 log half — merged), 2026-07-28
location: godot/src/Core/Definitions/UnitDefinition.cs:392,407; godot/src/Effects/AbilityCastSystem.cs:137,165,182
severity: medium
reason: ResolveAbilities still drops unknown ids and over-cap abilities with no log; TryCast (and now the aura/self-passive installers) bare-return on a bad registry index. Partly covered by AbilityWiringTeethTest; runtime diagnostic still absent. Adjacent-but-distinct: DW-107 (registry silent Empty), DW-121 (unresolved discovered factions). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle ability-cast-path-hardening (Epic 15, Story 15.9)

## Deferred from: story 2.4b (2026-06-28) — migrated 2026-07-28

### DW-286: Ally-targeted TargetUnit (heal-other) needs a target-affinity hint
origin: migrated from legacy ledger ("Deferred from: story 2.4b (2026-06-28)"), 2026-07-28
location: godot/src/Core/Definitions/AbilityDefinition.cs:30-113; godot/src/UI/SelectionSystem.cs:396,912
severity: low
reason: No TargetAffinity field exists anywhere; AbilityTargeting is still the 4-value enum; click-picker is enemy-only (FindNearestEnemyUnit, hardcoded "click an enemy target" prompt). No shipped content blocked. Feature, not a defect. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed as Story 15.11 (ability targeting increments), per Alec's fold-into-Epic-15 decision

### DW-287: Worker-cast command card (Decision C reversal)
origin: migrated from legacy ledger ("Deferred from: story 2.4b (2026-06-28)"), 2026-07-28
location: godot/src/UI/CommandCardSystem.cs:255-274
severity: medium
reason: Ability section hid for workers.
status: done 2026-07-28
resolution: Story 2.9b — the !workerSelected term is gone; abilitySelected computed independently; ability panel repositions to the stacked slot when co-displayed with the worker card. Verified 2026-07-28.

### DW-288: No per-entity UnitDefinition link for presentation ability reads (documentation note)
origin: migrated from legacy ledger ("Deferred from: story 2.4b (2026-06-28)"), 2026-07-28
location: godot/src/UI/CommandCardSystem.cs:1340-1360
severity: low
reason: The card behavior it documents still holds (reads AbilityCount/AbilityId SoA by focusId), but its load-bearing clause is now false — Story 3.17 added EntityWorld.SourceDefinition[id].
status: done 2026-07-28
resolution: Closed as an obsolete documentation note (correct-course 2026-07-28).

## Deferred from: code review of story-2.4b (2026-06-28) — migrated 2026-07-28

### DW-289: Ability (and faction) JSON not covered by the pre-match content handshake
origin: migrated from legacy ledger ("Deferred from: code review of story-2.4b (2026-06-28)"), 2026-07-28
location: godot/src/Core/Definitions/ContentHash.cs; godot/src/Core/Definitions/MatchAgreementHash.cs
severity: critical
reason: Determinism-relevant content could differ between peers unhashed.
status: done 2026-07-28
resolution: Story 9.16 — ContentHash covers factions + full AbilityRegistry + ItemRegistry + DamageTable (its own doc names this closed vector); folded into MatchAgreementHash, gated fail-closed client-side (HandshakeGate.CheckStart) and server-side (DedicatedServer.HandleReady, unparsable→0→fail); per-domain Breakdown string answers the opaque-HALT complaint. Residual registry silent-Empty is DW-107. Verified 2026-07-28.

### DW-290: Command-card disable-gate and press-handler targeting sets coupled by assumption
origin: migrated from legacy ledger ("Deferred from: code review of story-2.4b (2026-06-28)"), 2026-07-28
location: godot/src/UI/CommandCardSystem.cs:1370-1376,1419-1431
severity: low
reason: Still two separately-enumerated sets; safe while AbilityTargeting has exactly 4 members. No defect today.
status: done 2026-07-28
resolution: Closed as accepted-latent (correct-course 2026-07-28); carried as an explicit AC on Story 15.11 (fold both into one shared is-castable-targeting predicate when a 5th mode lands) — see DW-280.

## Deferred from: code review of story-2.5a (2026-06-29) — migrated 2026-07-28

### DW-291: Header fields editable-but-ignored in Advanced mode + Show JSON overwrites raw edits
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5a (2026-06-29)"), 2026-07-28
location: godot/src/CreationSuite/AbilityEditorPanel.cs:448-465,614-617,682-686
severity: medium
reason: Advanced-mode desync between header widgets, raw pane, and tree.
status: done 2026-07-28
resolution: ShowJson serializes BuildAdvancedModel() in Advanced (in-code comment names this deferral); Advanced→Simple reconciles via ResolveAdvancedDef + lossless preset gate; DoSave folds the dirty raw pane back tree-canonically. Verified 2026-07-28.

### DW-292: Advanced-mode Save wrote an un-sanitized content id
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5a (2026-06-29)"), 2026-07-28
location: godot/src/CreationSuite/AbilityEditorPanel.cs:687-697
severity: medium
reason: Filename ≠ id; ids could collide.
status: done 2026-07-28
resolution: Decision-#8 guard — SanitizeId mismatch and empty-after-sanitize both refuse the save with a located error. (Pattern available for DW-47's item-editor sibling, noted there.) Verified 2026-07-28.

### DW-293: WriteNode can emit DamageType.COUNT that Read rejects (write/read asymmetry, COUNT half)
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5a (2026-06-29)"), 2026-07-28
location: godot/src/Effects/EffectNodeJsonConverter.cs:184-189,228-229
severity: low
reason: The TargetFilter half is OBSOLETE (2.9a lifted the Read reject; Air/Ground/Structure now authorable). The COUNT half is narrower but real: WriteEnum has no guard while ReadNode hard-rejects COUNT; editor-unreachable (DraftVocabulary excludes COUNT by design). Stale contradicted comment at AbilityEditorPanel.Advanced.cs:280-281 ("NEVER the reserved bits — AC5"). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle ability-editor-composer-cleanup (Epic 15, Story 15.9)

## Deferred from: code review of story-2.5b (2026-06-29) — migrated 2026-07-28

### DW-294: All-empty Persistent node validates + saves as a no-op ability
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5b (2026-06-29)"), 2026-07-28
location: godot/src/CreationSuite/AbilityEditorPanel.Advanced.cs:293-294; godot/src/Core/Definitions/AbilityValidator.cs:280-282
severity: low
reason: Authoring footgun.
status: done 2026-07-28
resolution: Both recommended mitigations landed — composer dim note + Story 2.6 hard validator rule for passives. Active-ability residual (validator intentionally untouched per Decision #6) rides DW-278's warning channel. Verified 2026-07-28.

### DW-295: period_effect + period_ticks = 0 validates but never fires
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5b (2026-06-29)"), 2026-07-28
location: godot/src/Core/Definitions/AbilityValidator.cs:283-291
severity: medium
reason: Authoring footgun.
status: done 2026-07-28
resolution: In-UI hints (composer + modifier-period twin) plus validator rules rejecting period_ticks<=0 and the period_count<=0 sibling for while_alive. Active residual rides DW-278. Verified 2026-07-28.

### DW-296: Composer/Simple SpinBoxes display quantized values (display ≠ saved Fixed)
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5b (2026-06-29)" item 3 + "Deferred from: code review of story-2.5a (2026-06-29)" item 3 — merged), 2026-07-28
location: godot/src/CreationSuite/AbilityEditorPanel.cs:885-897; AbilityEditorPanel.Advanced.cs:125,258-266,279,348
severity: low
reason: Unchanged — Step=1/0.5 rows snap on ValueChanged; no data loss but display≠saved persists for loaded fractional values. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle ability-editor-precision-fidelity (Epic 15, Story 15.9)

### DW-297: DraftNode.Depth()/SearchAreaDepth() are Tier-1-tested but unused by production
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5b (2026-06-29)"), 2026-07-28
location: godot/src/CreationSuite/AbilityDraft.cs:277-309
severity: low
reason: Unchanged — only referenced from AbilityDraftTests; production re-derives via TreeCtx/CountNodes. Delete or pin semantics vs EffectCaps.MaxEffectDepth. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle ability-editor-composer-cleanup (Epic 15, Story 15.9)

### DW-298: Flag-combination multi-select (checkbox) UI for Filter/Status
origin: migrated from legacy ledger ("Deferred from: code review of story-2.5b (2026-06-29)"), 2026-07-28
location: godot/src/CreationSuite/AbilityEditorPanel.Advanced.cs:282,353,428-430,483
severity: low
reason: Single-select dropdowns couldn't author flag combinations.
status: done 2026-07-28
resolution: Story 2.6 Task 8 — AddFlagChecks checkbox sets for Filter and Status; former single-select builders removed (documented in-code). Verified 2026-07-28.

## Deferred from: code review of story-2.6 (2026-06-30) — migrated 2026-07-28

### DW-299: Periodic self-passive caps at 256 pulses, never renewed
origin: migrated from legacy ledger ("Deferred from: code review of story-2.6 (2026-06-30)"), 2026-07-28
location: godot/src/Effects/ModifierStore.cs:270-276
severity: high
reason: Lifelong regen passives went silently pulse-less.
status: done 2026-07-28
resolution: Story 2.13 AC4.1 — lifelong re-arm in the same slot when `_persistent[slot].Lifelong && HasPeriod`; flag plumbed through PersistentEffect/converter/CanonicalFold/validator; LifelongHotTests; shipped furnace content sets "lifelong": true; self-passives covered via the shared InstallPersistent machinery. (The plain-Modifier sibling remains open as DW-271 — distinct path.) Composer round-trip regression risk filed as DW-323. Verified 2026-07-28.

### DW-300: Self-passive spawn-install is not idempotent against a live re-ApplyUnitDefinition
origin: migrated from legacy ledger ("Deferred from: code review of story-2.6 (2026-06-30)"), 2026-07-28
location: godot/src/Effects/AbilityCastSystem.cs:161-171; godot/src/Effects/ModifierStore.cs:266-269
severity: medium
reason: Latent, unchanged — no install-once guard, no same-id dedup (2.13's own comment names it). Precondition holds (every mapper caller runs on a fresh Create slot) but the seam now has TWO subscribers (InstallSelfPassive + ResearchSystem.ApplyCompletedResearch) — a future in-place re-apply (upgrade/morph/tech) double-fires both. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle passive-install-idempotence (Epic 15, Story 15.7): 3-line dedup now beats waiting for the trap

### DW-301: Editor RestoreUnit silently dropped authored armor + passives
origin: migrated from legacy ledger ("Deferred from: code review of story-2.6 (2026-06-30)"), 2026-07-28
location: godot/src/Core/EntityWorld.cs:1080-1090
severity: high
reason: Undo/load lost authored state.
status: done 2026-07-28
resolution: Story 3.17 — restore routes through the single mapper (BaseArmor/EffectiveArmor + the three passive indices + install seam); snapshot deliberately captures Base stats. Verified 2026-07-28.

## Deferred from: code review of story-2.8 (2026-07-01) — migrated 2026-07-28

### DW-302: Local player hardcoded to Faction.Player1 in IssueTrainCommand
origin: migrated from legacy ledger ("Deferred from: code review of story-2.8 (2026-07-01)"), 2026-07-28
location: godot/src/UI/CommandCardSystem.cs:38-44,756-765
severity: high
reason: MP-hostile hardcode.
status: done 2026-07-28
resolution: Story 9.5 — _localFaction Func wired by CameraPhase to EffectiveLocalFaction; IssueTrainCommand guards ownership and passes _localFaction(); all sibling building seams converted. Cosmetic residual: the P1/P2 title ternary at :312 (label text only). Verified 2026-07-28.

### DW-303: ProductionQueueValue clamps a stored index ≥254 to the 255 fallback sentinel
origin: migrated from legacy ledger ("Deferred from: code review of story-2.8 (2026-07-01)"), 2026-07-28
location: godot/src/Economy/BuildingSystem.cs:419-427
severity: low
reason: Requires a ≥255-unit single-faction roster; documented accepted invariant.
status: done 2026-07-28
resolution: Closed as accepted design boundary (correct-course 2026-07-28); if creator rosters ever grow, a dedicated int[] chosen-index array belongs to a creator-content story.

### DW-304: OrderApplier building commands no-op silently when BuildingSystem is null
origin: migrated from legacy ledger ("Deferred from: code review of story-2.8 (2026-07-01)"), 2026-07-28
location: godot/src/Multiplayer/NetworkCommand.cs:208-216
severity: low
reason: Unchanged and the surface GREW — the null-elvis pattern spread from Train to SetRally (and the Revive/Research family): an accidental null wiring is a deterministic silent no-op indistinguishable from intentional headless-null. Unreachable today (wiring unconditional in MatchLifecycleController). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle lockstep-wiring-fail-loud (Epic 15, Story 15.7)

## Deferred from: code review of story-2.9a (2026-07-01) — migrated 2026-07-28

### DW-305: Editor RestoreUnit silently dropped authored AttackDomainOf
origin: migrated from legacy ledger ("Deferred from: code review of story-2.9a (2026-07-01)"), 2026-07-28
location: godot/src/Core/EntityWorld.cs:983,1070-1089
severity: medium
reason: Undo/load lost the authored attack domain. (Item 1 of this section merged into DW-242.)
status: done 2026-07-28
resolution: Story 3.17 — ParsedAttackDomains written inside the single mapper which RestoreUnit now calls; docstring enumerates attack domain among re-derived fields; old hand-enumerated carve-off note removed. Verified 2026-07-28.

## Deferred from: code review of story-2.10 (2026-07-02) — migrated 2026-07-28

### DW-306: Repeated Spike Transmutation self-cast could strand the Covenant Transmuter 0-HP-alive
origin: migrated from legacy ledger ("Deferred from: code review of story-2.10 (2026-07-02)"), 2026-07-28
location: godot/src/Effects/AbilityCastSystem.cs:199; godot/resources/data/abilities/spike_transmutation.json
severity: high
reason: Health-cost self-cast could reduce the caster below viability.
status: done 2026-07-28
resolution: Story 2.13 AC5.3/D-4 — HP-affordability gate (`!AllowSelfLethal && Health <= CostHealth → refuse`, atomic, pre-debit; in-code comment names this item); content converted to cost_health/allow_self_lethal + apply_modifier; validator negative-cost reject. Verified 2026-07-28.

## Deferred from: code review of story-2.13 (2026-07-05) — migrated 2026-07-28

### DW-307: A crowd of plain Move orders can still ring at ~1.0-1.7u without completing
origin: migrated from legacy ledger ("Deferred from: code review of story-2.13 (2026-07-05)"), 2026-07-28
location: godot/src/Navigation/MovementSystem.cs:17-24
severity: low
reason: Unchanged and deliberate — ARRIVE_THRESHOLD_SQR stays 0.5u² (widening strands melee; guarded by MeleeUnitBelowArriveRadius_StillClosesAndStrikes); only the two GOAL thresholds moved in 2.13. Plain-Move completion still rides the physical stop. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed to Story 10.14 (pathfinding quality / arrival stability); do not bundle

## Deferred from: code review of story-3.1a (2026-07-05) — migrated 2026-07-28

### DW-308: AccentController registry had no unregister/clear — leak in long-lived controllers
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1a (2026-07-05)"), 2026-07-28
location: godot/src/UI/Theme/AccentController.cs:86-89
severity: medium
reason: Freed StyleBoxes accumulated across accent switches.
status: done 2026-07-28
resolution: Story 3.1b — Unregister(box) + Clear() exist; ChimeraComponents.Reset() calls Clear() and PruneAccentHandlers runs on every accent switch/bind. Verified 2026-07-28.

### DW-309: UX-DR11 shadow tokens absent from the committed main.tres
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1a (2026-07-05)"), 2026-07-28
location: godot/assets/ui/main.tres; godot/src/UI/Theme/ThemeTokens.cs:171
severity: low
reason: Godot Theme constants are int-only — cannot hold size+offset+alpha+color; recipes are C#-side (ShadowRecipes + WithShadow).
status: done 2026-07-28
resolution: Closed as accepted-as-recorded (correct-course 2026-07-28); a .tres-native representation belongs to a future theme-format story (10.7) if ever wanted.

### DW-310: ChimeraStyleBox.Chamfer had no cut bounds guard
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1a (2026-07-05)"), 2026-07-28
location: godot/src/UI/Theme/ChimeraStyleBox.cs:29-34
severity: low
reason: Negative cut produced degenerate geometry.
status: done 2026-07-28
resolution: Story 3.1b D-5 — `cut = Mathf.Max(0, cut)` with a comment naming this deferral; upper bound intentionally left to Godot's draw-time radius cap. Verified 2026-07-28.

### DW-311: cut-lg (14) not exercised in the 3.1a in-engine proof
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1a (2026-07-05)"), 2026-07-28
location: godot/src/UI/Components/ChimeraDialog.cs:75-76
severity: low
reason: AC2 named it; only the preview used it.
status: done 2026-07-28
resolution: cut_lg is now a shipped production surface — every ChimeraDialog modal builds from Const(ThemeTokens.CutLg); the 3.1c gallery /godot-verify (dialog over scrim) was the in-engine proof. Verified 2026-07-28.

## Deferred from: code review of story-3.1c (2026-07-06) — migrated 2026-07-28

### DW-312: Tooltip position is a one-shot snapshot — keyboard-focus tooltips don't re-anchor on scroll
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1c (2026-07-06)"), 2026-07-28
location: godot/src/UI/Components/ChimeraTooltip.cs:87-88,118
severity: low
reason: The punt condition materialized — UnitCardPanel wraps its form in a ScrollContainer with 35 tooltip attach sites; a keyboard-focus tip stays anchored to a stale rect until FocusExited. Decide hide-on-scroll vs re-anchor. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed to Story 10.6 (accessibility, keyboard-focus behavior home); bundle ui-tooltip-anchor-on-scroll if pulled earlier

### DW-313: Toast stack is uncapped — burst spam can grow it off-screen
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1c (2026-07-06)"), 2026-07-28
location: godot/src/UI/Components/ChimeraToastHost.cs:79-85
severity: low
reason: NextY() still sums over every entry with no max-visible cap/evict/coalesce (3.1c patch added reflow-tween discipline only). Self-healing/transient. Verified 2026-07-28.
status: done 2026-07-30
resolution: already resolved: ChimeraToastHost.cs:33 MaxVisibleToasts=5 + :106 while(_toasts.Count>MaxVisibleToasts) evict — landed in Story 11.4
decision: 2026-07-28 correct-course — filed to Story 11.4 (event cues — the toast→real-MP-event wiring story owns the cap/evict policy)

### DW-314: ChimeraComponents.Reset() could call into a freed AccentController on scene reload
origin: migrated from legacy ledger ("Deferred from: code review of story-3.1c (2026-07-06)"), 2026-07-28
location: godot/src/UI/Components/ChimeraComponents.cs:77,104-114
severity: medium
reason: Pre-existing reload-safety hole.
status: done 2026-07-28
resolution: Story 3.3 review — IsInstanceValid guard around unsubscribe+Clear (lists/caches still dropped unconditionally); IsInitialized also validates the instance. Comments credit the 3.1c deferral. Verified 2026-07-28.

## Deferred from: code review of story-3.2 (2026-07-06) — migrated 2026-07-28

### DW-315: HeroStore per-tick SimChecksum fold
origin: migrated from legacy ledger ("Deferred from: code review of story-3.2 (2026-07-06)"), 2026-07-28
location: godot/src/Core/SimChecksum.cs:430-460
severity: high
reason: The AC2-deferred planned fold.
status: done 2026-07-28
resolution: Story 3.13 — HeroStore mutable state folded (v11, ascending HeroId, count-driven) incl. the 3.14 revival fields in the same bump; coverage teeth in SimChecksumCoverageGuardTest.AssertHeroStoreFoldedIntoChecksum; goldens re-pinned through each bump (now v21). Verified 2026-07-28.

## Deferred from: code review of story-3.3 (2026-07-06) — migrated 2026-07-28

### DW-316: Model-reference readout could claim "Renders <path>" while the preview shows the box placeholder
origin: migrated from legacy ledger ("Deferred from: code review of story-3.3 (2026-07-06)"), 2026-07-28
location: godot/src/UI/MeshLoader.cs:42-75; godot/src/CreationSuite/UnitCardPanel.Edit.cs:951-965
severity: medium
reason: Readout and preview resolved the mesh independently.
status: done 2026-07-28
resolution: Story 3.5 — MeshLoader gained `out bool usedPlaceholder` (true for missing path AND loaded-but-no-MeshInstance3D); the panel consumes the live render outcome (MeshError, D-3); the stale string no longer exists — Model row is a LineEdit + Browse/Box + located ChimeraValidationBadge. Verified 2026-07-28.

### DW-317: LoadFactionFromPath leaves FactionDefinition.LoadFromFile's parse/IO throw uncaught
origin: migrated from legacy ledger ("Deferred from: code review of story-3.3 (2026-07-06)"), 2026-07-28
location: godot/src/CreationSuite/UnitCardPanel.cs:165; godot/src/CreationSuite/BuildingCardPanel.cs:117
severity: medium
reason: Same defect class as DW-62 (root: no try/catch around Deserialize in LoadFromFile) and DW-123 (ScenarioLoadPhase call site); still live at both card-panel call sites. Fixing DW-62 at the root subsumes these; co-locate DW-65's _history clear (same method). Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — bundle faction-load-fail-closed = faction-load-error-handling (DW-62, DW-123) + these call sites (Epic 15, Story 15.6)

### DW-318: Forward-note: LoadFactionFromPath File.Exists silent no-op in an exported .pck
origin: migrated from legacy ledger ("Deferred from: code review of story-3.3 (2026-07-06)"), 2026-07-28
location: godot/src/CreationSuite/UnitCardPanel.cs:157
severity: low
reason: The forward condition resolved false — Story 3.4 did NOT reuse this entry point; repo-wide grep shows zero callers (editor-only /godot-verify harness). The generic System.IO-on-res:// concern belongs to export/packaging stories.
status: done 2026-07-28
resolution: Closed as obsolete (correct-course 2026-07-28).

## Deferred from: dev of story-3.4 (2026-07-06) — plain numbered items migrated 2026-07-28

### DW-319: UX-DR33 primitive logged: ChimeraValidationBadge
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-28
location: godot/src/UI/Components/ChimeraValidationBadge.cs
severity: low
reason: Log-the-primitive ask.
status: done 2026-07-28
resolution: Over-delivered — consumed by 9 panels / 35 badge sites with 5.9 multi-badge fan-out. The optional ChimeraComponents.ValidationBadge factory promotion was not done (cosmetic API consistency; dropped — fold into 10.7 only if convenient). Verified 2026-07-28.

### DW-320: Numeric-field undo granularity is focus-session, not per-arrow-click
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-28
location: godot/src/CreationSuite/UnitCardPanel.Edit.cs:360-392
severity: low
reason: Unchanged (snapshot on FocusEntered, one undo entry on FocusExited; pure arrow-button tweaks not individually undoable); replicated across sibling editors. Save persistence correct. Verified 2026-07-28.
status: open
decision: 2026-07-28 correct-course — filed to Stories 10.6/10.7 (UI polish); bundle editor-undo-granularity if pulled earlier

### DW-321: Undo-of-delete restores in-memory order but render slots re-materialize on Save→reload
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-28
location: godot/src/Core/Sim/ScenarioApplier.cs:489
severity: low
reason: MeshType slot drift across save/reload.
status: done 2026-07-28
resolution: Story 3.10 — MeshType derives from the single FactionDefinition.IndexOfUnit coordinate at apply time everywhere; ResetToAuthoredStart clears + re-applies through the same applier; undo re-inserts at the original index so IndexOfUnit is stable. Verified 2026-07-28.

### DW-322: Whole-faction-file re-indent on Save is git-diff noise
origin: migrated from legacy ledger ("Deferred from: dev of story-3.4 (2026-07-06)"), 2026-07-28
location: godot/src/Core/Definitions/FactionWriter.cs:105
severity: low
reason: Determinism-harmless as documented (no faction-file byte hash pinned; CanonicalModelHash folds the model); unknown-key preservation enforced by the token-preserving Put*/ApplyFields path.
status: done 2026-07-28
resolution: Closed as accepted (correct-course 2026-07-28) — won't-fix.

## New findings — correct-course verification pass (2026-07-28)

### DW-323: Advanced ability composer silently DROPS PersistentEffect.Lifelong on round-trip
origin: correct-course legacy-verification pass, 2026-07-28
location: godot/src/CreationSuite/AbilityDraft.cs:216-219,258-265
severity: high
reason: DraftNode has no Lifelong field — ToEffectNode constructs PersistentEffect with lifelong defaulting false and FromEffectNode doesn't capture it; no Lifelong anywhere in AbilityEditorPanel*. Opening furnace_trickle/furnace_pour in Advanced and saving strips the flag, silently re-introducing the 256-pulse defect Story 2.13 fixed; the validator cannot catch it (only rejects lifelong-without-period). Found while verifying DW-299's closure.
status: open
decision: 2026-07-28 correct-course — bundle ability-composer-lifelong-round-trip (Epic 15, Story 15.3): DraftNode.Lifelong + composer checkbox + round-trip test

### DW-324: Stale-comment / doc-debt sweep surfaced by the verification pass
origin: correct-course legacy-verification pass, 2026-07-28
location: various (see reason)
severity: low
reason: Doc rot found while verifying closures: EffectCaps.cs:8,79,87 still say "reserved to fold into the Epic-9 rulesetHash" (9.4 folded them); Modifier.cs:48 says "0 = instantaneous" (see DW-270); ModifierStore.cs:39 describes the re-entrancy guard as unbuilt without noting the validator fence; ServerChecksumCollector.cs:11,22-23 stale "MaxSlots 4→8 in 9.2" (deliberately NOT bumped — sim ceiling 8 vs MP seat ceiling 4); AbilityEditorPanel.Advanced.cs:280-281 "NEVER the reserved Air/Ground/Structure bits" contradicted since 2.9a; the 2.9b fallback-seed entry's dead ApplyFallback:159-160 anchor; ScenarioLoadPhase.cs:440 comment-coupled fallback start positions; lan-desync-smoke.ps1 missing "requires source/DEBUG build" banner; optional FallbackMirror-vs-alpha_map_01 agreement test.
status: open
decision: 2026-07-28 correct-course — rides bundle housekeeping-docs-and-normalization (DW-46, DW-105, DW-175) (Epic 15, Story 15.6)

### DW-457: The skirmish map/faction catalog scans `res://` content via `System.IO.Directory` over…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
reason: The skirmish map/faction catalog scans `res://` content via `System.IO.Directory` over `ProjectSettings.GlobalizePath`, which resolves to a real OS directory only in the editor/unpacked runtime — in an exported PCK build those resources are packed and `System.IO` sees no directory, so `SkirmishCatalog.ScanMaps`/`ScanFactions` return empty and the skirmish screen shows "No maps found". — Evidence: `SkirmishCatalog` uses `Directory.GetFiles`; `SkirmishSetupOverlay.Initialize` passes `ProjectSettings.GlobalizePath(res://…)`. This is the same repo-wide pattern already used by `FactionDefinition.LoadSelectableFromDirectory` and the MainScene faction seeding, so it is not unique to this story — but it must migrate to PCK-aware `DirAccess` (or an editor-only gate) before any exported client build ships (Epic 10 release/export work, 10-5/10-8).
status: open

### DW-458: `SkirmishSetupToScenario.Build` drops Open/Closed player slots but leaves the base map's triggers, win-condition…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
reason: `SkirmishSetupToScenario.Build` drops Open/Closed player slots but leaves the base map's triggers, win-condition spec, and scenario entities byte-identical — so a shipped map authored with per-slot triggers or a per-player-elimination win condition referencing a now-dropped slot boots with dangling references the setup screen never warns about. — Evidence: `Build` rebuilds only `PlayerSlots` (per its own doc-comment) and the transform has no trigger/win-condition reconciliation; reachable if a 3–4-start shipped map with per-slot trigger/win-condition logic is launched as a 1v1 (2 active slots). Low probability today (win-condition presets are mostly last-team-standing), needs a design decision on prune-vs-reject, so deferred rather than patched.
status: open

### DW-459: The entire `MainScene` skirmish match-start orchestration — the async `_Ready` handoff…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
reason: The entire `MainScene` skirmish match-start orchestration — the async `_Ready` handoff, `activePlayers`/`FactionRegistry` sizing from the in-memory `PendingGeneratedScenario`, the AI-level override, the read-then-clear of the skirmish statics, and the fail-safe re-open (`FailSafeSkirmishBoot` / `_bootAborted` / `_bootPending`) — has zero automated coverage, so a regression in the story's headline flow (e.g. re-sizing the registry from the stale on-disk `ScenarioPath`, or the fail-safe not clearing `PendingGeneratedScenario`) would pass the full Tier-1 suite green. — Evidence: All Story-11.1 tests are Godot-free core (validator/transform/catalog/runner); grep for `LaunchSkirmish`/`PendingSkirmish`/`_bootAborted`/`_bootPending` across the test projects returns nothing. The pure decisions (rawSlots from the in-memory scenario; the fail-safe state transition) could be extracted to Godot-free helpers and pinned, mirroring how the `ScenePhaseRunner` progress seam was extracted; live in-engine verification for Epic 11 is otherwise deferred to Epic 10 per project record.
status: open

### DW-460: The setup-screen slot swatch (`SkirmishSetupOverlay.SlotColorFor(i)` keyed by the map's start-position ROW index)…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
reason: The setup-screen slot swatch (`SkirmishSetupOverlay.SlotColorFor(i)` keyed by the map's start-position ROW index) drifts from the actual in-match team color whenever the active (Human/AI) slots are not row-0-contiguous — because `SkirmishSetupToScenario.Build` renumbers active slots to a CONTIGUOUS 0..k-1 span and the in-match color is keyed by that post-transform contiguous index, not the row index. — Evidence: After this follow-up pass's color/order patches, the human always renders Player1=blue in-match, but its setup swatch = `SlotColorAt(rowIndex)` — e.g. a human placed in row 2 (rows 0/1 Open) shows a green swatch while playing blue. This directly contradicts PATCH 7's stated invariant ("the setup swatch can never drift from the in-match team color"). Cosmetic only (a preview swatch), and the correct fix is to color each swatch by the slot's rank among currently-active slots (recomputed on Revalidate) — more than a trivial review patch, so deferred.
status: open

### DW-461: Dev/test scratch scenarios that ship in `res://resources/data/scenarios/` (e.g
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
reason: Dev/test scratch scenarios that ship in `res://resources/data/scenarios/` (e.g. `123.json` "Alpha Skirmish" and `my-new-map.json` "My New Map", each with 2 authored `player_slots`) now surface as selectable, launchable maps on the skirmish setup screen, because `SkirmishCatalog.ScanMaps` lists every parseable `*.json` with ≥1 start position and there is no curation allow-list. — Evidence: `SkirmishCatalog.ScanMaps` filters only maps with 0 start positions; `123.json`/`my-new-map.json` both have 2 `player_slots` so they pass. The story intent explicitly scopes the list to "shipped `res://…/scenarios/*.json` only" (mod.io/curation deferred), so the CODE is per-intent — the real issue is content hygiene: dev scratch files live in the shipped scenarios dir. Fix = either remove/relocate scratch maps out of the shipped dir, or add a curated shipped-map manifest/flag. Pre-existing content surfaced incidentally by this story's new map list; not caused by the diff.
status: open

### DW-462: The in-match team-color slot→palette mapping (`FactionVisualsPhase.SlotColorAt` / `SlotColor(Faction) =…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
location: SimSources.props
reason: The in-match team-color slot→palette mapping (`FactionVisualsPhase.SlotColorAt` / `SlotColor(Faction) = SlotColorAt((int)faction - 1)`) has no automated regression test, despite having shipped INVERTED twice during this story (PATCH 7 and follow-up-2), each time caught only by human review. — Evidence: grep of `godot/**/*Tests*.cs` for `SlotColor`/`SlotColorAt`/`FactionVisuals` returns nothing; `FactionVisualsPhase` is excluded from the Tier-1 glob (`SimSources.props`) and returns a Godot `Color`, so a headless RGBA-equality test needs a Godot-free palette seam (extract the raw `(r,g,b)` palette + the `-1` index shift into `src/Core/**` and have `FactionVisualsPhase` consume it). The mapping is currently correct; this is a missing regression net for a demonstrated recurring high-impact bug, not a live defect — deferred because the fix is a presentation refactor beyond a surgical review patch.
status: open

### DW-463: A skirmish launch of `alpha_map_01` reaches Play with only 2 of the 3 units the map authors for start position 0…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-1-the-real-skirmish-setup-screen-loading-match-start-flow.md`
reason: A skirmish launch of `alpha_map_01` reaches Play with only 2 of the 3 units the map authors for start position 0 (the `mage` at x=-40,z=0 alongside the two workers), in BOTH a same-faction and a cross-faction launch. Not attributed to Story 11.1 — it survives the transform, so the loss is downstream in the applier/Play-entry path. — Evidence: In-engine gate 2026-07-28. Post-launch HUD at `Tick 0/1 [PLAY]` reads `P1: 2 units` for every skirmish launch of `alpha_map_01`, whose `units[]` authors 3 entries for slot 0 (worker, worker, mage) and 2 for slot 1. `SkirmishSetupToScenario.Build` keeps every unit whose slot maps and the slot-0 remap is the identity path (`Build_SameFactionLaunch_LeavesPrePlacedUnitIdsUntouched`), so all 3 are emitted into the built `ScenarioData` — the drop happens after the transform. The pre-launch boot HUD shows `P1: 3` but reads `[EDIT]` and carries a "Placing: P1 [Covenant Transmuter]" placement ghost, so it cannot be used as the control: the EDIT-vs-PLAY comparison may be counting a ghost rather than proving the mage spawns on the legacy path. Needs an applier-level check (does `ScenarioApplier` spawn all 3 slot-0 units, and does Play entry despawn one?) before it can be assigned to this story or confirmed pre-existing. Distinct from the cross-faction roster defect fixed in this story — that one is closed and regression-tested.
status: open

### DW-464: The ONLINE concede path has two unpolished robustness/UX gaps — (a) `LockstepManager.EnqueueConcede`'s online…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-2-in-match-menu-pause-game-speed-concede-surrender-leave-victory-defeat-score-screen.md`
reason: The ONLINE concede path has two unpolished robustness/UX gaps — (a) `LockstepManager.EnqueueConcede`'s online branch silently drops the concede order when `_pendingCount >= TickCommandPacket.MAX_ORDERS` (returns false, `MainScene.IssueConcede` ignores it, no retry/feedback), and (b) after a confirmed online concede the menu closes immediately with no "surrendering…" feedback while the verdict only latches a tick-round-trip later, so the player is dropped back into a still-live match as if nothing happened. — Evidence: Surfaced by the Story 11.2 adversarial + edge-case review. Both are online-only: the offline path applies the concede immediately (verified in the in-engine gate — P1→LOST resolves next tick), so neither is reachable in the currently-shippable offline skirmish. Live MP is explicitly deferred to Epic 9 (the spec scopes online as "not in-engine verifiable now"), so these are genuine but un-exercisable now. Fix when the MP shell lands: buffer/retry a dropped concede (a rare high-intent order must not be lost on a busy tick) and give the online player pending-surrender feedback (disable Concede / show "Surrendering…") until the verdict latches.
status: open

### DW-465: Cold-boot load a saved game from the main menu (not just mid-match), rebuilding the scenario from the save…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-3-sp-save-load-full-world-serializer-slots-autosave-format-stability.md`
reason: Cold-boot load a saved game from the main menu (not just mid-match), rebuilding the scenario from the save header's persisted SkirmishSetup launch record via SkirmishSetupToScenario.Build. — Evidence: Story 11.3 wired in-match Save→Load (the FR-67 mid-match target) and IssueLoad reuses the current match's _ctx.Scenario; the header already persists MapId + per-slot Kind/FactionId/Team/Ai (SaveGameHeaderData.ToSkirmishSetup) but no load-from-menu entry point consumes it, so a save can only be loaded while already in a match on the identical scenario.
status: open
### DW-466: SP saves are not portable across machines and the format cannot detect it; make AiOpponentSystem Fixed-point (or…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-3-sp-save-load-full-world-serializer-slots-autosave-format-stability.md`
reason: SP saves are not portable across machines and the format cannot detect it; make AiOpponentSystem Fixed-point (or fold a platform/runtime marker into the save header) before advertising cross-machine saves. — Evidence: AiOpponentSystem scoring runs on float (_aggressionWeight, ScoreLaunchAttack), which is not deterministic across CPU/JIT; the save header stamps only algo versions, so a save loaded on another machine can pick a different AI action on the first resumed tick and desync undetectably. 11.3 documents saves as same-machine for 1.0; this is the same float→Fixed dependency already noted for lockstep MP AI.
status: open
### DW-467: Move full-world save serialization + disk I/O off the game thread (background write of an already-captured buffer)…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-3-sp-save-load-full-world-serializer-slots-autosave-format-stability.md`
location: godot/CLAUDE.md
reason: Move full-world save serialization + disk I/O off the game thread (background write of an already-captured buffer) to avoid an autosave frame hitch. — Evidence: SaveGameState.CaptureFrom allocates ~70 arrays and SaveGameFile.Write does blocking File.WriteAllBytes + File.Replace, all synchronously in IssueSave / the _Process autosave branch; at the 500-2000 entity target (godot/CLAUDE.md) this will produce a visible hitch on every 120 s autosave.
status: open

### DW-468: The in-match minimap panel renders off-screen at 1920×1080 (root Control anchored bottom-right with zero offsets…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
reason: The in-match minimap panel renders off-screen at 1920×1080 (root Control anchored bottom-right with zero offsets pins its 200×200 body to the (1920,1080) corner), so the minimap — including pre-11.4 dots/fog and the new 11.4 Alt-click pings, camera-view box, and alert flash — is not visible. — Evidence: Confirmed in-engine during the 11.4 gate drive; the layout lives in `MinimapPhase` (not touched by 11.4), so this is pre-existing. 11.4's minimap LOGIC executes correctly (Alt-click `_gui_input` fires `OnLocalPing`, `AddPing`/`FlashAlert` run, `GetViewBounds` yields valid coords) but the panel itself is off-screen. SURFACE TO ALEC.
status: open

### DW-469: The 256-entry `CombatEventQueue` ring silently drops events when full, and denial/completion cues now share that…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
reason: The 256-entry `CombatEventQueue` ring silently drops events when full, and denial/completion cues now share that lossy queue with no priority — a large single-tick battle (>256 hit pushes) can drop a denial or training-complete cue. — Evidence: `CombatEventQueue.MAX_EVENTS=256` with silent drop-when-full (pre-existing); 11.4 adds `OrderDenied`/`TrainingComplete`/`ResearchComplete` consumption to the same queue. Reserving headroom or prioritising non-hit events is a queue-design change beyond this story's scope; self-healing and low-likelihood.
status: open

### DW-470: On the P2P transport path `SendMapPing` writes `LocalFaction` into the packet and the receiver trusts `buf[1]`…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
reason: On the P2P transport path `SendMapPing` writes `LocalFaction` into the packet and the receiver trusts `buf[1]` verbatim; a modified P2P client could forge a ping's origin faction/color. — Evidence: The `DedicatedServer` relay re-stamps the authoritative faction (anti-spoof), but the P2P receive path does not mirror it. MP anti-spoof hardening; MP is not shipping-verified for 1.0.
status: open

### DW-471: A pure spectator/observer (whose `EffectiveLocalFaction` clamps to Player1) receives Player1's under-attack…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
reason: A pure spectator/observer (whose `EffectiveLocalFaction` clamps to Player1) receives Player1's under-attack toasts, minimap flashes, and denial/ack cues as if they owned that faction. — Evidence: `MatchAlertBridge.Update` filters on `_localFaction()` which clamps observers to Player1 (pre-existing clamp). Spectator mode is not a 1.0-verified surface; low severity.
status: open

### DW-472: **RESOLVED 2026-07-30** — `MainScene.PendingLoadedSave` (static) survived an early-return launch-gate veto and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
location: MainScene.cs:2288
reason: **RESOLVED 2026-07-30** — `MainScene.PendingLoadedSave` (static) survived an early-return launch-gate veto and leaked into the next, unrelated skirmish. — Evidence: Found by the post-merge ultra-review of 11-3/11-4 (2026-07-29), `MainScene.cs:2288`. Fix shape: clear the static in a `finally` (or at entry, into a local) so no early-return path can leave it armed. Not reproduced live; reasoned from the control flow.
status: resolved
resolution: Split the body into `ResetToAuthoredStartCore` and wrapped it in a `try/finally` that disarms the statics (with a "Load discarded" notice) on any non-completing exit. Deliberately a FAILURE-path sweep, not consume-at-entry: `LaunchSkirmish` reaches this via `GetTree().ReloadCurrentScene()`, so the statics must survive the reload for whichever reset call actually enters Play — capturing at entry would let an earlier boot-sequence reset swallow the load. Success path unchanged, verified in-engine by a save/load round trip (loaded an autosave at tick 3599 from a live match at tick 2954; post-reload tick 3717 — it jumped UP past the pre-load tick, which a restart cannot do).

### DW-473: **RESOLVED 2026-07-30** — `SaveGameState.Validate()` bounded free-list LENGTHS for all five stores but never their…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-3-sp-save-load-full-world-serializer-slots-autosave-format-stability.md`
location: SaveGameState.cs:980
reason: **RESOLVED 2026-07-30** — `SaveGameState.Validate()` bounded free-list LENGTHS for all five stores but never their ELEMENTS. — Evidence: Found by the post-merge ultra-review (2026-07-29), `SaveGameState.cs:980`; `RestoreAllocation`/`RestoreManagement` copy the lists verbatim. `EntityWorld.Create()` then pops `_freeList[--_freeCount]` and writes at that index (IndexOutOfRange), or a duplicate id hands one slot to two spawns. Only reachable from a corrupt/hand-edited save — the fail-closed posture says it should still be caught at validate time.
status: resolved
resolution: A `FreeList(name, list, cap)` local now range-checks every element and rejects duplicates, wired for entity/building/hero/item/projectile. Regression tests `Validate_FreeListEntryOutOfRange_`/`FreeListEntryNegative_`/`DuplicateFreeListEntry_ThrowsWithMessage` plus a clean-list positive; verified load-bearing (they fail with the fix reverted).

### DW-474: **RESOLVED 2026-07-30** — `RestoreResources`/`RestoreResearch`/`RestoreWinState` bounded their loops by one lane…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-3-sp-save-load-full-world-serializer-slots-autosave-format-stability.md`
location: SaveGameState.cs:697
reason: **RESOLVED 2026-07-30** — `RestoreResources`/`RestoreResearch`/`RestoreWinState` bounded their loops by one lane while indexing every sibling at the same `i`. — Evidence: Found by the post-merge ultra-review (2026-07-29), `SaveGameState.cs:697`, `:782`, `:804`. Safe on the disk path only because `Validate` ran first, but `RestoreInto` is public and the in-memory `CaptureFrom`→`RestoreInto` path (used by tests) never validates. Bound each array by its own length, or make `RestoreInto` validate.
status: resolved
resolution: Each loop now takes the minimum across every lane it actually reads (including the per-faction jagged inner lengths in `RestoreResearch`), so the public `RestoreInto` is safe on the in-memory path that never calls `Validate`.

### DW-475: **RESOLVED 2026-07-30** — `CaptureBuildings` stored the live `ShopStock` `string[]` by reference and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-3-sp-save-load-full-world-serializer-slots-autosave-format-stability.md`
location: SaveGameState.cs:328
reason: **RESOLVED 2026-07-30** — `CaptureBuildings` stored the live `ShopStock` `string[]` by reference and `RestoreBuildings` aliased it straight back. — Evidence: Found by the post-merge ultra-review (2026-07-29), `SaveGameState.cs:328` / `:690`. Harmless on the disk path (serialization copies), but every other reference-typed lane in the file is cloned or round-tripped by id — this one is inconsistent and a latent aliasing bug for any future in-memory snapshot use (rollback, MP state sync).
status: resolved
resolution: Both directions now clone (empty stock still shares `Array.Empty<string>()`). Regression test `CaptureThenRestore_DoesNotAliasShopStockArrays` mutates the live store after capture and the restored store after restore, asserting the state object is untouched; verified load-bearing.

### DW-476: **RESOLVED 2026-07-30** — `SelectionSystem.ConfirmOrder` acked unconditionally, so a Shift-queued order onto full…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
location: SelectionSystem.cs:634
reason: **RESOLVED 2026-07-30** — `SelectionSystem.ConfirmOrder` acked unconditionally, so a Shift-queued order onto full order rings produced the confirmation AND the `QueueFull` refusal for one click. — Evidence: Found by the post-merge ultra-review (2026-07-29), `SelectionSystem.cs:634`. Note the issue-time-optimism itself is the deliberate GDD §6 design (and was rejected as a finding during 11.4's own review); the defect is only the contradictory DOUBLE feedback on the one guard that rejects synchronously at issue time. Cheapest fix: suppress the ack when the local ring-full precheck already knows the order cannot be queued.
status: resolved
resolution: `ConfirmOrder` takes a `queued` flag and, when set, acks only if some selected unit still has ring room — reading the same FOLDED `OrderQueueCount` the guard rejects on, so the two cannot disagree. Threaded through the four queued-capable issue paths (move, attack-move, attack-target, attack-building). Issue-time optimism is retained everywhere else; this covers only the guard that rejects synchronously at issue time.

### DW-477: **RESOLVED 2026-07-29** — Alt+LMB on the minimap dropped a ping AND panned the camera to the pinged point
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-4-under-attack-alerts-minimap-pings-event-cues-denial-acknowledgment-feedback.md`
location: MinimapBridge.cs:248
reason: **RESOLVED 2026-07-29** — Alt+LMB on the minimap dropped a ping AND panned the camera to the pinged point. — Evidence: Found by the post-merge ultra-review (2026-07-29), `MinimapBridge.cs:248`, then CONFIRMED IN-ENGINE the same day: an Alt+LMB at minimap px (1750, 1030) moved the camera pivot to `(-79.36, 0, 74.24)`, the exact world point clicked. Root cause was wider than first described: the Alt branch consumed only the PRESS, so BOTH the matching release (the pan branch never tested `mb.Pressed`) and any drag leaked through. FIXED by a `_pingGesture` latch that swallows the remainder of the gesture, self-healing on a plain press so a release delivered outside the Control cannot strand it. Re-verified in-engine: Alt+click and Alt+drag both leave the pivot at `(0,0,0)`; a plain click still pans to `(-79.36, 0, 74.24)`.
status: resolved

### DW-478: Depth-5 production queue lets a player queue N units against unchanged live SupplyUsed (supply is consumed at…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-6-production-queue-depth-5-with-queue-display-and-cancel-refund.md`
location: godot/src/Economy/BuildingSystem.cs:422-427
reason: Depth-5 production queue lets a player queue N units against unchanged live SupplyUsed (supply is consumed at spawn, never reserved at enqueue), so queuing can overshoot the supply cap by up to 4. — Evidence: BuildingSystem.TrainUnit (godot/src/Economy/BuildingSystem.cs:422-427) gates each enqueue with resources.HasSupply against SupplyUsed, which is recomputed from LIVE units and never incremented by enqueue; five enqueues therefore all see the same headroom. At depth-1 (2.8) at most one order was ever in flight so overshoot was impossible — the depth-5 widening (this story) introduces it. Deferred, not fixed: reserving supply on enqueue (WC3-strict) vs consume-at-spawn (the existing model, kept deliberately) is a design decision the intent does not specify; consequence is a self-correcting balance overshoot (own-ore overspend, deterministic across peers), not a crash/desync/free-resource exploit.
status: open

### DW-479: If SpawnTrainedUnit no-ops at the EntityWorld entity cap, TickProduction still calls AdvanceQueue and discards the…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-6-production-queue-depth-5-with-queue-display-and-cancel-refund.md`
location: godot/src/Economy/BuildingSystem.cs:192-193
severity: low
reason: If SpawnTrainedUnit no-ops at the EntityWorld entity cap, TickProduction still calls AdvanceQueue and discards the paid-for head order with no refund — and now advances the whole depth-5 queue rather than a single order. — Evidence: BuildingSystem.TickProduction (godot/src/Economy/BuildingSystem.cs:192-193) calls SpawnTrainedUnit then AdvanceQueue unconditionally. Pre-existing at depth-1 (the 2.8 code likewise spawned-then-reset the single slot), so not introduced here, but the depth-5 queue makes a stalled spawn burn slots tick by tick. Low: only reachable at the entity cap (500-2000 sim entities), rare in normal play.
status: open

### DW-480: CancelTrain addresses a queue slot by POSITION resolved at exec-tick; under lockstep input delay a head completion…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-6-production-queue-depth-5-with-queue-display-and-cancel-refund.md`
location: godot/src/Economy/BuildingSystem.cs:517
reason: CancelTrain addresses a queue slot by POSITION resolved at exec-tick; under lockstep input delay a head completion between click and exec can refund/remove the newly-promoted unit instead of the one the player clicked. — Evidence: CommandCardSystem.IssueCancelTrainCommand packs the slot index into the wire (TargetX); BuildingSystem.CancelTrainCommand (godot/src/Economy/BuildingSystem.cs:517) resolves head+slot at exec-tick. In offline/SP (the only currently-live path) apply is immediate so there is no window; the race only opens under MP lockstep delay, which is Epic 9 territory (MP is not yet verified). WC3 cancels by position with the same theoretical race and it is accepted UX. Deferred for MP-cancel UX hardening; deterministic across peers (no desync).
status: open

### DW-481: A unit authored with TrainTime <= 0 makes a non-empty head whose timer is 0; TickProduction's "timer <= 0 ->…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-6-production-queue-depth-5-with-queue-display-and-cancel-refund.md`
location: godot/src/Economy/BuildingSystem.cs:183-184
reason: A unit authored with TrainTime <= 0 makes a non-empty head whose timer is 0; TickProduction's "timer <= 0 -> continue" guard skips it forever, and it now also blocks the four slots queued behind it. — Evidence: BuildingSystem.TickProduction (godot/src/Economy/BuildingSystem.cs:183-184): idle is detected by an empty head slot, then a non-empty head with ProductionTimer <= 0 is skipped. At enqueue an idle head's timer is set to def.TrainTime (:452-453); TrainTime <= 0 -> timer 0 -> permanent skip. Pre-existing content-validation gap (no validator enforces TrainTime > 0); depth-5 widens the blast radius from one stuck order to the whole queue. Fix belongs in content validation, out of this story's scope.
status: open

### DW-482: SettingsData.MigrateForward stamps CurrentSchemaVersion unconditionally, so a settings.json written by a newer…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-7-video-settings-the-mode-select-honesty-strip.md`
reason: SettingsData.MigrateForward stamps CurrentSchemaVersion unconditionally, so a settings.json written by a newer build (higher schema) is silently downgraded and its forward-only fields dropped on the next Save. — Evidence: MigrateForward has no `if (SchemaVersion > CurrentSchemaVersion)` bail; pre-existing since Story 8.1, surfaced by the 11.7 review. Real cross-build data-loss on a version downgrade.
status: open

### DW-483: Editor/creation-suite panels (center-anchored, fixed CustomMinimumSize) are unverified at high UI-scale (1.5x) and…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-7-video-settings-the-mode-select-honesty-strip.md`
reason: Editor/creation-suite panels (center-anchored, fixed CustomMinimumSize) are unverified at high UI-scale (1.5x) and 4K — the new UI-scale lever can shrink the logical viewport below a panel's fixed size and clip its controls. — Evidence: Story 11.7 re-anchored only the HUD command-card panels; editor panels were not touched or driven at a non-default scale. AC3 names "editor layouts" and a 1080p/1440p/4K x 2-scale matrix; the in-engine gate sampled only 1080p + scale 1.5 on HUD.
status: open

### DW-484: The Graphics-tab Resolution dropdown stays enabled in Borderless/Fullscreen where ApplyVideo only issues…
origin: migrated from flat appender bullet, 2026-07-30 (A1-E11)
source_spec: `_bmad-output/implementation-artifacts/spec-11-7-video-settings-the-mode-select-honesty-strip.md`
location: godot/src/UI/SettingsManager.cs
reason: The Graphics-tab Resolution dropdown stays enabled in Borderless/Fullscreen where ApplyVideo only issues WindowSetSize in windowed mode, so a resolution pick in those two modes silently no-ops with no UI feedback (no grey-out / hint). — Evidence: SettingsManager.ApplyVideo (godot/src/UI/SettingsManager.cs) gates WindowSetSize behind `mode == WindowMode.Windowed` (a review-1 fix, correct — resizing fights borderless/exclusive). The review-2 patch stopped the spurious safe-revert arm for an inert resolution change, but the control itself still offers a setting it ignores in 2 of 3 modes. Real UX gap surfaced by the 11.7 review; fix (disable the resolution OptionButton on a window-mode `item_selected` when the mode is not windowed) is a live-interaction enhancement beyond any AC, so deferred rather than expanded into this story.
status: open
