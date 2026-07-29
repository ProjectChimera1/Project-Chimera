---
title: 'Story 11.3 — SP save/load: full-world serializer + slots/autosave/format stability'
type: 'feature'
created: '2026-07-29'
status: 'done'
baseline_revision: 'ca9da3686adc7df90bb8230c0ab2aba1aa066937'
final_revision: 'c43eee23a9cd3f8563eb1413ea75f2bf0cddec7a'
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-11-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** After 11.2 the in-match menu carries **disabled** Save/Load buttons stubbed "coming in 11.3". Single-player has no way to save a match in progress and resume it later. FR-67 requires a checksum-verified mid-match SP save/load: a save must capture the *entire* mutable simulation and reload it so the resumed match is byte-identical to one that never stopped.

**Approach:** Add a Godot-free full-world serializer that captures every mutable sim store off `SimulationHost` into a versioned, section-tagged binary blob and restores it into a freshly-scenario-applied host. Load reuses the existing setup-phase spine (11.1): re-run `SkirmishSetupToScenario.Build` from the persisted `SkirmishSetup`, let the phase runner reconstruct all stores/derived state/compiled triggers, then overlay the saved mutable state (the `HeroProfileLoader.LoadInto` precedent). The one hard sub-problem — `ModifierStore` slots hold live descriptor **object references** with no stable id — is solved by a deterministic **canonical effect-descriptor table** built from the (content-identical-on-load) ability/item/faction content, so each active modifier/persistent slot round-trips by index. Wire the manual Save/Load buttons + a slot picker + a periodic autosave slot into the in-match menu (SP-only, disabled online), all from the 3.1x kit.

## Boundaries & Constraints

**Always:**
- **Byte-identical resume is the acceptance floor.** After loading a save taken at tick K, the next 300+ ticks must produce a `SimChecksum` stream byte-identical to an uninterrupted reference run from the same start, asserted headless in Tier-1. Prove it with in-memory checksum-stream comparison (`GoldenChecksumReplay`/`SimResetTests` helpers), not a committed golden.
- **Capture the full mutable sim, not just the checksum-folded subset.** The checksum is NOT the save manifest. Persist, off `SimulationHost`: `EntityWorld` (all SoA arrays incl. free list, `_nextId`/`HighWaterMark`, `AliveCount`, and the RNG `ulong` state), `BuildingStore`, `ModifierStore`, `HeroStore`, `ItemStore`, `ProjectileStore` (unfolded but authoritative), `ResourceStore`, `ResourceNodeStore`, `ResearchStore` (jagged per-faction), `WinStateStore` + win-condition config, `AllianceStore`, `TriggerEnabledStore`, the DSL runtime (`DslVarTable`, `DslLoopState`, `DslEventQueue`), the `ScenarioDirector` mutable trigger runtime (`_triggerFired`, `_triggerCooldown`, `_firstTick`), `AiOpponentSystem` per-match decision state, and the tick counter (`SimulationLoop.CurrentTick`). Store every value as its integer/`Fixed.Raw`/enum representation — **no float ever**, no object-graph walking.
- **Rebuild derived/authored state on load; do not serialize it.** `ModifierSystem` stat-bonus accumulators, fog/pathability/elevation grids, compiled trigger IR + regions + win-condition config, all transient per-tick queues (`CombatEventQueue`, death feed, `DslSimEventFeed`), `EntityWorld.PrevPosition`, presentation copies (`DslVarReadback`, `TriggerFireLog`, `MatchStats`) are re-derived by the scenario re-apply + first tick, never read from the save. Restore the folded `Effective*` stats directly (they are authoritative folded state) and mark the accumulators clean, OR recompute from the restored `ModifierStore` — whichever reproduces the folded stats exactly.
- **Reference content by stable string id and re-resolve on load** through the registries (`FactionRegistry`/`ItemRegistry`/`AbilityRegistry`/`UnitDefinition` id), exactly as `HeroProfileLoader.ReMintInventory` does — never persist a volatile packed index or object ref. Reference-typed SoA (`EntityWorld.SourceDefinition`/`FeedbackProfile`, `HeroStore.SourceDef`, `ProjectileStore.Feedback`) restore by re-routing the def-id through `EntityWorld.ApplyUnitDefinition`; modifier/persistent descriptors restore by the canonical descriptor-table index (below).
- **Modifier/persistent descriptor re-resolution is deterministic and content-driven.** Build a read-only `CanonicalEffectDescriptorTable` by walking all loaded modifier/persistent-granting content (abilities, items, faction signature mechanics) in a fixed traversal order and assigning each `Modifier`/`PersistentEffect` descriptor a stable index; the serializer stores that index per active slot; load re-resolves `_modifier`/`_persistent` from it. The table is derived purely from content that is byte-identical across the save (guarded by the header's `ContentHash`), so indices are stable.
- **Versioned, fail-closed binary format.** Mirror the `.chmr` replay container: `magic(4)` + `formatVersion(2)` header; the header stamps `CanonicalModelHash` + `ContentHash` + each relevant `AlgoVersion` (`SimChecksum`, `CanonicalModelHash`, `StartStateHash`) + tick + the launch record (`SkirmishSetup`: `MapId` + per-slot `Kind`/`FactionId`/`Team`/`Ai`); body = length-framed, type-tagged sections terminated by a zero-length frame. The reader throws `InvalidDataException` with a clear, user-facing message on bad magic, older/newer `formatVersion`, a mismatched `AlgoVersion`/`ContentHash`, an unknown section tag, or truncation — never a silent partial load or a desyncing best-effort resume (the `ReplayPlayer` precedent).
- **Load drives the existing phase spine unchanged.** Reuse `MainScene.LaunchSkirmish` + `PendingGeneratedScenario` + `ReloadCurrentScene`; the setup phases (Terrain → Navigation → ScenarioLoad → FlowFieldInit …) reconstruct stores, then a post-phase overlay applies the saved state (mirroring `HeroProfileLoader.LoadInto` at `MainScene` ~L646). A load failure fails safe back to the originating screen surfacing the located `Validated<T>`/`InvalidDataException` message — never a hang, black screen, or crash — via the existing `FailSafeSkirmishBoot` path.
- **SP-only; online disabled.** Save/Load and autosave are gated on `!_ctx.Lockstep.IsOnline`; online, the buttons are visible-but-disabled (mirror how 11.2 disables Speed online in `InMatchMenuOverlay.SetOnline`). Autosave never runs online.
- **Godot-free sim core.** The serializer, capture/restore, and the descriptor table live under `godot/src/Core/**` (or `Core/Persistence/`), stay `using Godot;`-free, and are pulled into the Tier-1 assembly via `SimSources.props`. Disk I/O and slot enumeration are a thin Godot-free core over an injected absolute directory (the `LocalProfileSource`/`ReplayBrowserPanel` pattern); the Godot phase resolves `user://saves/` via `ProjectSettings.GlobalizePath`.
- **All new UI composes from the 3.1x kit** (`ChimeraComponents` + `ChimeraDialog`, `EnsureKitInitialized` first), mirroring `InMatchMenuOverlay`/`SettingsPanel`.

**Block If:**
- Achieving byte-identical resume would require folding new state into `SimChecksum`/`MatchAgreementHash`, bumping any `AlgoVersion`, or re-baselining a golden. HALT `blocked`, condition `save/load requires a determinism fold`. (Save/load reads existing folded + unfolded state; it must add zero goldens and touch no `AlgoVersion` — the version pins in `SimResetTests.HashAlgoVersions_AreUnchanged` must stay put.)
- Deterministic modifier/persistent descriptor re-resolution cannot be achieved with a read-only canonical table over loaded content — i.e. it would require mutating authored definitions, adding a non-deterministic id, or a content-model schema change beyond building a derived lookup. HALT `blocked`, condition `modifier descriptor round-trip needs a content-model change`.
- The load path cannot reuse the existing setup-phase spine without adding or reordering a phase pinned by `PhaseOrderTest`. HALT `blocked`, condition `load requires a setup-phase order change`.

**Never:**
- MP save/load (post-1.0), replay-based save, or recording save/speed as tick-stamped events. Save/Load render disabled online.
- Cross-format-version compatibility or migration. A `formatVersion` bump is a documented save-break (fail-closed with an in-UI message), not a migrate-forward (that is the `SettingsData` model, explicitly the wrong one here).
- Serializing derived/authored/transient state (accumulators, grids, compiled IR, per-tick queues, presentation copies) or persisting content by packed index/object ref instead of stable id.
- Changing the tick rate, `FixedDt`, sim system order, `SimChecksum` coverage, or any hash `AlgoVersion`; re-baselining any golden/checksum file.
- Adding/reordering a setup phase, or reconstructing sim state by any path other than scenario re-apply + overlay.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Save (offline) | `[PLAY]` SP, menu open, Save → slot chosen | Full-world blob written to `user://saves/slot_<n>.chsav`; menu confirms; match continues uninterrupted | Write error → kit error toast/dialog, match continues |
| Load (offline) | Menu → Load → slot chosen | Scene reloads via the phase spine; stores reconstructed then overlaid with saved state; match resumes at the saved tick | Missing/corrupt file → fail safe to originating screen with the located message |
| Byte-identical resume | Save at tick K, load into fresh host, run to K+300 | `SimChecksum` stream K+1..K+300 byte-identical to an uninterrupted reference run | Divergence at any tick = test failure (names first divergent tick) |
| Resume with active modifiers | Save with a live timed `Modifier` + `PersistentEffect` (DoT/HoT) mid-schedule | Descriptor refs re-resolved by canonical index; stack counts, remaining ticks, period schedules restored exactly; resume byte-identical | Descriptor index absent in table → fail-closed load error |
| Autosave (offline) | SP match running, autosave interval elapsed | Blob written to the dedicated `autosave.chsav` slot without interrupting play | Write error logged; play continues; no crash |
| Version mismatch | Load a `.chsav` with an older/newer `formatVersion` | Rejected with a clear "save made by a different game version" message; no partial load | Fail-closed `InvalidDataException` surfaced in UI |
| Content drift | Load a save whose `ContentHash`/`CanonicalModelHash` ≠ current content | Rejected with a clear "the map/content this save used has changed" message | Fail-closed; back to originating screen |
| Unknown section | Load a `.chsav` with an unrecognized section tag at the pinned version | Rejected as corrupt; fail-closed | `InvalidDataException`, no partial apply |
| Online menu | Online branch, menu open | Save/Load visible but disabled; autosave inactive | — |
| Format stability | Save → load → save the same match state | Second blob byte-identical to the first (round-trip stable) | Mismatch = test failure |

</intent-contract>

## Code Map

- `godot/src/Core/Sim/SimulationHost.cs` -- root object graph (:60-148 store fields); `ClearForReset()` (:342-371) enumerates every store to round-trip; `Create(...)` (:171) headless construction; `StepOnce()`/`SetChecksumSink`/`ChecksumInterval` (:398/:407/:161) for the test; `CurrentTick`/tick counter via `_loop`.
- `godot/src/Core/SimulationLoop.cs` -- `CurrentTick` (uint, :50) = the tick to persist; `ResetTick()` (:118). Not folded → must be captured/restored explicitly.
- `godot/src/Core/EntityWorld.cs` -- all SoA arrays (:196-633), free list `_freeList`/`_freeCount` (:664/665), `_nextId`/`HighWaterMark` (:663/671), `AliveCount` (:668), `Rng` (`SimRng`, :193); `ApplyUnitDefinition` (:948) = the single def→SoA mapper for restoring reference-typed fields; `SnapshotUnit`/`RestoreUnit` (:1045/1082) = the existing def-re-resolution precedent.
- `godot/src/Core/SimRng.cs` -- single `ulong _state` (:29), `State` (:35), `Seed` (:41). Capture/restore the one ulong.
- `godot/src/Effects/ModifierStore.cs` -- folded int arrays `_modifierId`/`_remainingTicks`/`_ticksUntilPeriod`/`_periodsRemaining`/`_stackCount` + `_count`; NON-folded `_modifier`/`_persistent` (descriptor refs, :58-59), `_casterId`/`_casterFaction` (:60-61). `Apply`/`InstallPersistent` (:127/:195) show slot layout; needs restore-slot accessors + descriptor re-point. `Clear()` (:362) = the clean baseline.
- `godot/src/Core/BuildingStore.cs` / `HeroStore.cs` / `ItemStore.cs` / `Combat/ProjectileStore.cs` / `Core/ResourceStore.cs` / `ResourceNodeStore.cs` / `Core/ResearchStore.cs` / `WinStateStore.cs` / `AllianceStore.cs` / `TriggerEnabledStore.cs` -- SoA + free-lists + `Generation` counters + jagged arrays (`ResearchStore.CompletedLevels`/`Cumulative*`, `BuildingStore.ShopStock`/`DefinitionId` string arrays) to capture/restore. Reference `HeroStore.SourceDef`, `ProjectileStore.Feedback` by def-id.
- `godot/src/Core/ScenarioDirector.cs` -- mutable trigger runtime that IS NOT folded but must persist: `_triggerFired` (:57), `_triggerCooldown` (:58), `_firstTick` (:236); compiled `_execs`/subscriptions/regions are rebuilt by `LoadScenario` (do not serialize).
- `godot/src/AI/AiOpponentSystem.cs` -- per-match decision state `_productionBuildingIds`/`_cmdCenterExpId`/`_attackCooldown` (:68/71/73), reset by `_ai.ResetForMatch()`; capture/restore.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- `Apply(Validated<ScenarioData>)` (:129) reconstructs all stores in determinism order + `ScenarioDirector.LoadScenario` last (:339); the object the overlay writes over.
- `godot/src/Core/Skirmish/SkirmishSetup.cs` / `SkirmishSetupToScenario.cs` -- the launch record to persist (`MapId` + slots) and the deterministic rebuild (`Build`, :26) to reconstruct the identical scenario on load.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` / `ContentHash.cs` / `StartStateHash.cs` -- header stamps + `AlgoVersion` pins for drift detection.
- `godot/src/Multiplayer/ReplayRecorder.cs` / `ReplayPlayer.cs` / `ReplayHeader.cs` -- the binary container + fail-closed version-guard + cheap header-only reader to MIRROR.
- `godot/src/Core/Definitions/LocalProfileSource.cs` / `godot/src/UI/ReplayBrowserPanel.cs` -- Godot-free-core-over-injected-dir + slot-listing precedents.
- `godot/src/UI/InMatchMenuOverlay.cs` -- `_saveBtn`/`_loadBtn` (:48-49) disabled at :130-135; event pattern (:25-35); `SetOnline` (:188-200) online-gating precedent; `OpenConfirm` (:214-224) dialog pattern for the slot picker.
- `godot/src/Core/MainScene.cs` -- `WireSessionShell` (:774-802) = subscribe `OnSave`/`OnLoad`; `LaunchSkirmish` (:759-768) = load path reuse; `_Process` offline branch (:1221-1229) = autosave hook; `ResetMatchOnReturnToEdit` (:2300) = dismiss overlay; `_ctx.Lockstep.IsOnline` = online detection; hero-overlay post-phase apply (~L646) = overlay-timing precedent.
- `godot/src/Core/Bootstrap/Phases/GameOverOverlayPhase.cs` / `SceneContext.cs` / `HeroPickerPhase.cs` -- overlay construction site + `_ctx` handle + the `user://` disk-rail (`GlobalizePath`) pattern.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` / `Sim/SimResetTests.cs` / `Golden/GoldenApplierScenario.cs` -- reuse `RunAndRecord`/`CompareSequences`/`AssertSameSequence`/`BuildApplied()`/`BuildModel()` (300-tick scenario) for the resume test; `SimSources.props` pulls new `src/Core/**` files into Tier-1.

## Tasks & Acceptance

**Execution — sim core (Godot-free, Tier-1):**
- `godot/src/Core/Persistence/CanonicalEffectDescriptorTable.cs` -- NEW: deterministic walk of loaded modifier/persistent-granting content (abilities, items, faction signature mechanics) assigning each `Modifier`/`PersistentEffect` a stable index; `IndexOf(descriptor)→int` + `Get(int)→descriptor`. -- makes modifier slots serializable by index.
- `godot/src/Effects/ModifierStore.cs` -- add restore accessors: read a slot's `_casterId`/`_casterFaction` + descriptor, and a `RestoreSlot(id, slot, modifierId, remaining, ticksUntilPeriod, periodsRemaining, stackCount, casterId, casterFaction, Modifier?, PersistentEffect?)` + `SetCount(id,n)` that rebuild `[0,_count)` without re-running install effects. -- overlay without re-triggering InitialEffect.
- `godot/src/Core/Persistence/SaveGameState.cs` -- NEW: `CaptureFrom(SimulationHost)` → an in-memory POCO of every mutable store's arrays/free-lists/counters/RNG/tick (integers/`Fixed.Raw`/enum only; modifier descriptors as canonical indices; reference-typed SoA as def-ids); `RestoreInto(SimulationHost, CanonicalEffectDescriptorTable)` overlays it onto a scenario-applied host and re-derives accumulators/grids clean. -- the capture/restore core.
- `godot/src/Core/Persistence/SaveGameFile.cs` -- NEW: `Write(Stream, SaveGameState, header)` + `Read(Stream)→(header, SaveGameState)` versioned section-tagged binary (magic `CHSV`, `FormatVersion=1`), header stamps `SkirmishSetup` + `CanonicalModelHash`/`ContentHash`/`AlgoVersion`s + tick; fail-closed reader (bad magic/version/hash/unknown-section/truncation → `InvalidDataException` with a clear message). -- the format.
- `godot/src/Core/Persistence/SaveGameHeader.cs` -- NEW: cheap header-only reader (mirror `ReplayHeader`) for the slot-list UI (map name, tick/duration, timestamp) without a full parse; never throws (returns an "unreadable" sentinel). -- slot browser metadata.
- `godot/src/Core/Persistence/ISaveStore.cs` + `LocalSaveStore.cs` -- NEW: Godot-free slot enumeration/read/write over an injected absolute dir (`List()`, `Read(slot)`, `Write(slot, bytes)`, `Delete(slot)`), mirroring `LocalProfileSource`. -- disk rail.
- `godot/ProjectChimera.Sim.Tests/Persistence/**` -- NEW xUnit: (a) **byte-identical resume** — save at tick K, load into a fresh scenario-applied host, run to K+300, assert the checksum stream equals the uninterrupted reference (reuse `SimResetTests`/`GoldenChecksumReplay` helpers); (b) the same with a live timed `Modifier` + `PersistentEffect` injected before save (descriptor round-trip); (c) format fail-closed cases (bad magic, older/newer version, content-hash mismatch, unknown section, truncation) each throw with a message; (d) round-trip **format stability** (save→load→save byte-identical); (e) `AlgoVersion` pins unchanged + no golden/`SimChecksum` file touched. -- the acceptance proof.

**Execution — presentation (Godot-coupled, in-engine gated):**
- `godot/src/UI/InMatchMenuOverlay.cs` -- enable `_saveBtn`/`_loadBtn` (drop `Disabled=true`, real handlers, replace tooltips); add `OnSave`/`OnLoad` events; gate their enabled-state on `!online` in `SetOnline`; add a `ChimeraDialog`-based slot picker (choose slot to save/load, showing header metadata). -- the menu surface.
- `godot/src/UI/SaveLoadOverlay.cs` -- NEW kit overlay (or extend the menu) listing save slots with metadata for save target / load source selection. -- slots UI.
- `godot/src/Core/MainScene.cs` -- subscribe `OnSave`/`OnLoad` in `WireSessionShell`; `IssueSave(slot)` (capture host → `SaveGameFile.Write` → `LocalSaveStore.Write`); `IssueLoad(slot)` (read+validate header, stash the `SaveGameState` + `SkirmishSetup` in statics, `LaunchSkirmish`-reload; after the phase runner completes, overlay via `SaveGameState.RestoreInto` — mirror the hero post-phase apply at ~L646); autosave accumulator in the offline `_Process` branch (SP-only, interval-gated, writes `autosave` slot); dismiss the overlay in `ResetMatchOnReturnToEdit`; route load failures through `FailSafeSkirmishBoot` surfacing the located message. -- the wiring.
- `godot/src/Core/Bootstrap/Phases/GameOverOverlayPhase.cs` + `SceneContext.cs` + `HeroPickerPhase.cs`-style disk rail -- construct `SaveLoadOverlay`, resolve `user://saves/` via `GlobalizePath`, build `LocalSaveStore`, store both on `_ctx`. -- lifecycle + disk path.

**Acceptance Criteria:**
- Given a SP match saved at tick K, when it is loaded into a freshly scenario-applied host and run 300 more ticks, then the per-tick `SimChecksum` stream is byte-identical to an uninterrupted reference run — including a match carrying an active timed `Modifier` and `PersistentEffect` at save time.
- Given a `.chsav` with a mismatched `formatVersion`, `ContentHash`, `CanonicalModelHash`, an unknown section, or truncation, when it is loaded, then the load is rejected fail-closed with a clear user-facing message and no partial state is applied.
- Given `[PLAY]` SP, when the player opens the in-match menu, chooses Save and a slot, then a `.chsav` is written and the match continues uninterrupted; when the player later chooses Load and that slot, then the scene reloads through the setup-phase spine and the match resumes at the saved tick and state.
- Given a SP match running with autosave, when the autosave interval elapses, then the dedicated autosave slot is written without interrupting play; and autosave never runs online.
- Given the online branch, when the in-match menu is opened, then Save/Load are visible but disabled and no autosave occurs.
- Given the full Tier-1 suite, when it runs, then all new persistence tests pass, every hash `AlgoVersion` pin is unchanged, and no `SimChecksum` golden is re-baselined.

## Design Notes

**Why re-apply-then-overlay (not construct-from-save).** Re-running `SkirmishSetupToScenario.Build` from the persisted `SkirmishSetup` + `ScenarioApplier.Apply` rebuilds every store with wired deps (`OnDestroy` hooks, `ModifierSystem`, executors), compiled trigger IR + regions + win config (`ScenarioDirector.LoadScenario`), and the elevation/pathability grids — all the authored/derived state a save must NOT carry. The overlay then blasts the saved mutable arrays over the freshly-applied stores (full replace, not merge — the saved match's entity population, free list, and high-water mark differ from the fresh scenario). This is the `HeroProfileLoader.LoadInto` pattern lifted to the whole world, and it keeps the save blob to pure mutable state.

**The modifier descriptor round-trip (the hard part).** `ModifierStore` slots point at `Modifier`/`PersistentEffect` descriptor objects (used every tick for period pulses and expiry stat-revert); persistent instances carry no id at all (`_modifierId=0`). Because loaded content is byte-identical across a save (the header's `ContentHash` enforces it, fail-closed), a deterministic content walk produces the same descriptor objects in the same order every time — so a stable index into that walk is a safe serialization key. `RestoreSlot` re-points the descriptor and sets the folded fields directly, WITHOUT re-running `InitialEffect` (a restore is not a re-cast). If some content path can grant a descriptor unreachable by the canonical walk, that is the `modifier descriptor round-trip needs a content-model change` Block-If.

**Zero fold, zero golden.** Save/load only reads and writes existing state; it folds nothing new and moves no golden. Prove resume equivalence with in-memory `CompareSequences`, assert the `AlgoVersion` pins (`SimChecksum=21`, `CanonicalModelHash=14`, `StartStateHash=2`) are untouched, and confirm `git status` shows no golden/checksum file changed.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all pass incl. the new byte-identical-resume, modifier-round-trip, fail-closed-format, and format-stability tests; `AlgoVersion` pins unchanged; no golden re-baseline.
- `dotnet build godot/godot.csproj` -- expected: serializer + overlay + MainScene edits compile with no banned-API/AOT analyzer regressions.

**Manual checks (in-engine, gated — Epic-11 per-story gate via `/godot-verify` / godot-mcp bridge):**
- Launch a SP skirmish → in `[PLAY]` open the menu, Save to a slot; note the HUD tick/hash. Play on, then Load that slot → confirm the scene reloads through the loading screen and the HUD resumes at the saved tick with the same hash trajectory. Verify against numbers (saved tick vs resumed tick; `SimChecksum` continuity; the diff shows no golden/checksum file), per the in-engine gate discipline. Confirm Save/Load render disabled on an online-branch menu.

## Review Triage Log

### 2026-07-29 — Post-merge ultra-review follow-up (logged in the 11-4 spec)

A post-merge ultra-review over `ca9da36..e6a3273` found a **high** defect in this story that the five in-story review
layers missed: the `ScenarioDirector` change-detection snapshots (`_prevFlags` / `_prevBuildingDone`) are not
serialized and were left seeded from the AUTHORED board after a load, so the first resumed tick re-fired
`building_completed` for every player-built building — mutating folded state and making this story's byte-identical
resume claim false in any scenario with building triggers. Fixed by `ScenarioDirector.ReseedChangeDetection`, with a
load-bearing regression test (`SaveLoad_ResumeByteIdentical_WithMidMatchBuiltBuilding`). Three lower-severity save/load
findings were deferred to `deferred-work.md`. Full triage lives in the 11-4 spec's Review Triage Log.

### 2026-07-29 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 2, medium 8, low 1)
- defer: 3
- reject: 0
- addressed_findings:
  - `[high]` `[patch]` **In-engine gate FAIL — load produced a fresh match from tick 0.** `SaveGameState.RestoreInto` ran in the post-phase `_Ready` tail (~L683), but the Edit→Play toggle (`RequestSkirmishLaunch → ModeChanged → ResetToAuthoredStart → ClearForReset`, ~L755) then wiped the restored state and reset the tick — so in the running game a loaded save always began at the authored tick-0 start (observed HUD counting 12→27→51, not the saved tick). Tier-1 tests missed it because they call `RestoreInto` directly with no Edit→Play transition. Fixed: `RestoreInto` moved to the LAST step of `ResetToAuthoredStart` (after `ClearForReset` + authored re-apply), so the restored state is what enters `[PLAY]`. Re-verified in-engine: save at Tick 117 → load → resumed Tick 119 (saved+2 frames), forward hash `0x671C2C6A` matches the uninterrupted run at the overlapping tick. **PASS.**
  - `[high]` `[patch]` **Corrupt/inconsistent saves crashed the skirmish boot instead of failing closed.** `Read`/`ReadBody`/`ReadSection` validated primitive framing but not cross-array/scalar consistency, so a malformed-but-parseable save detonated in `RestoreInto` with `IndexOutOfRange`/`NRE`. Fixed: added a `Validate(ctx)` pre-mutation gate (every count ≤ store cap; jagged lane cardinality == enum COUNT, no null lanes; cross-array length consistency; `HostId`/slot ranges; byte/sbyte element ranges) throwing located `InvalidDataException`; `ModifierStore.RestoreSlot` rejects out-of-range host/slot. Tests: count-over-cap, short jagged lane, out-of-range modifier host.
  - `[medium]` `[patch]` **No integrity check over the body** — only the header was hashed, so a flipped body byte loaded as valid state. Fixed: FNV-64 body checksum stamped in the header, verified before parse/restore; `Load_FlippedBodyByte` test.
  - `[medium]` `[patch]` **Missing section restored silent defaults** — `ReadBody` stopped at the terminator without checking all sections present. Fixed: seen-section mask requiring all sections; `Load_MissingRequiredSection` test.
  - `[medium]` `[patch]` **Byte-identical proof covered only the economy slice** — the golden scenario exercised no deaths/heroes/items/research/projectiles/DSL, so ~half the restore paths ran on default data. Fixed: added resume tests for free-list + bumped-generation recycling (save-after-kill), hero+research+projectile+DSL var/timer, and a fired run_once trigger + DSL — all byte-identical over 300 ticks.
  - `[medium]` `[patch]` **ModifierStore restore dirty-flag asymmetry** — `RestoreSlot→AccumulateBonus` marks hosts dirty, unlike a long-stable modifier's host in a reference run. Addressed: a long-stable-modifier resume test proves `RecomputeEntity` is idempotent w.r.t. every folded field (writes only `Effective*`, never re-heals Health), so no dirty-flag restoration is needed.
  - `[medium]` `[patch]` **`CaptureModifiers` skipped empty in-count slots** (`continue`), rebuilding a short count. Fixed: an empty slot within `[0,count)` now throws (corrupt-state).
  - `[medium]` `[patch]` **MatchStats never captured** — resume showed a zeroed 11.2 score screen. Fixed: `MatchStats.CaptureCounters`/`RestoreCounters` + section; `SaveLoad_PreservesMatchStatsCounters` test.
  - `[medium]` `[patch]` **Save failures swallowed to the console** (incl. the descriptor Block-If and silent autosave failure). Fixed: `IssueSave`/`IssueLoad` surface failures via a HUD notice, distinguishing the descriptor Block-If (`InvalidOperationException`) from I/O errors.
  - `[medium]` `[patch]` **Load errors logged, not surfaced** — fixed: load rejections now surface the located message rather than only `GD.PrintErr`.
  - `[low]` `[patch]` **Slot picker `List()`/`PathFor` name transforms disagreed** — fixed: `List()` returns only names that round-trip through `PathFor`.
- **defer (3):** cold-boot load-from-main-menu (the header already persists the `SkirmishSetup` launch record for it; in-match load is the FR-67 target); AI float-determinism → cross-machine save portability (pre-existing `AiOpponentSystem` float limitation; saves documented same-machine for 1.0); synchronous serialization on the game thread (perf hitch at high entity counts). Appended to `deferred-work.md`.
- **reject (0).**

## Verification — In-Engine Gate (independent review-layer drive)

### In-Engine Gate - 2026-07-29
- surface: SP mid-match save/load (Story 11.3) — in-match menu Save/Load, `LocalSaveStore` disk rail, `SaveGameFile`/`SaveGameState` round-trip, reload-and-restore wiring in `MainScene._Ready` / `ResetToAuthoredStart`.
- launched: SP skirmish (Alpha Skirmish, Slot 1 Human / Slot 2 AI, Crucible Covenant) → `[PLAY]`; C# rebuilt (`dotnet build godot/godot.csproj` succeeded) then `godot_editor_edit stop`→`run`; menu + slot picker driven by emitted Button `pressed` signals; time held via `godot_game_time` freeze/step; HUD reads gated on the MainScene instance id changing on reload (1319276386858 → 11408020347947) so the post-reload match is measured, not the boot scene.
- digest: Save SLOT 2 at HUD `Tick 117 Hash 0x3F13A92A`; on-disk `2.chsav` header tick 117 (`75 00 00 00`), magic `CHSV`, version 1, simAlgo 21 / modelAlgo 14 / startAlgo 2, MapId `ALPHA_MAP_01`. Original run advanced to `Tick 162 Hash 0x671C2C6A`. Load SLOT 2 → resumed `Tick 119` ~20 frames post-reload (saved 117 + 2 elapsed), then advanced to `Tick 161 Hash 0x671C2C6A`. Load picker showed `SLOT 1 — TICK 115` / `SLOT 2 — TICK 117` enabled, `AUTOSAVE — NO SAVE` disabled.
- asserted: header tick == HUD save tick (117 == 117); new MainScene id on reload; **the resumed tick returns to the saved tick immediately (119, not counting up 4→12 from 0 as it did pre-fix)** — this is the defect the ordering fix closes; forward hash trajectory matches the uninterrupted run at the overlapping tick (`0x671C2C6A`); Save/Load enabled offline with correct per-slot metadata + empty-slot gating; no error-severity messages during load. Hash reads `—` for a moment right after resume because `RestoreTick` zeroes `LastChecksum` until the next checksum boundary (by design).
- caveat: the online-branch asymmetry (Save/Load disabled) was not exercised — a live online match needs the dedicated server + a peer, unavailable in a single-client editor session; the offline-enabled arm and the code-keyed `online` gate are confirmed. A pre-fix leftover `0.chsav` correctly shows "NO SAVE" (fails closed on the new body-hash header field).
- result: PASS

## Auto Run Result — dev-auto (2026-07-29)

**Summary:** Implemented Story 11.3 — single-player mid-match save/load — as a Godot-free full-world serializer plus session-shell wiring. `SaveGameState.CaptureFrom(host)` snapshots every mutable sim store off `SimulationHost` (all `EntityWorld` SoA arrays incl. free list/`_nextId`/`AliveCount`/RNG, Building/Hero/Item/Projectile/Resource/ResourceNode/Research/WinState/Alliance/TriggerEnabled stores, the DSL runtime, `ScenarioDirector` trigger runtime, AI decision state, tick counter, and MatchStats) as int/`Fixed.Raw`/enum values — no float, no object-graph walk. The one hard sub-problem — `ModifierStore` slots hold descriptor object refs with no stable id — is solved by a deterministic `CanonicalEffectDescriptorTable` (content-walk index) so active modifiers/persistents round-trip. `SaveGameFile` is a versioned, section-tagged binary (`CHSV` v1) with a fail-closed reader (bad magic/version/algo/content-hash/unknown-section/truncation/body-hash → located `InvalidDataException`). Load re-runs the existing setup-phase spine (scene reload → scenario re-apply) then overlays the saved state as the LAST step of `ResetToAuthoredStart`. The 11.2 disabled Save/Load buttons are wired to a slot picker + a 120 s autosave slot, SP-only (disabled online). No new fold, no golden re-baselined.

**Files changed (one line each):**
- `godot/src/Core/Persistence/CanonicalEffectDescriptorTable.cs` — NEW: deterministic content-walk assigning each `Modifier`/`PersistentEffect` a stable index; `IndexOf→-1` is the descriptor Block-If.
- `godot/src/Core/Persistence/SaveGameState.cs` — NEW: `CaptureFrom`/`RestoreInto` core; length-framed section body; `Validate(ctx)` fail-closed pre-restore gate; seen-section mask; MatchStats section.
- `godot/src/Core/Persistence/SaveGameFile.cs` — NEW: `CHSV` v1 container; header stamps `CanonicalModelHash`/`ContentHash`/algo pins + tick + `SkirmishSetup` + FNV-64 body hash; fail-closed `Read`.
- `godot/src/Core/Persistence/SaveGameHeader.cs` — NEW: cheap never-throwing header peek for the slot browser.
- `godot/src/Core/Persistence/ISaveStore.cs` + `LocalSaveStore.cs` — NEW: Godot-free slot rail over an injected dir; `List()`/`PathFor` name transforms consistent.
- `godot/src/Core/EntityWorld.cs` / `BuildingStore.cs` / `HeroStore.cs` / `ItemStore.cs` / `Combat/ProjectileStore.cs` / `ResourceNodeStore.cs` / `Effects/ModifierStore.cs` / `SimulationLoop.cs` / `Core/Sim/SimulationHost.cs` — additive capture/restore hooks (free lists, generations, tick, RNG, modifier slot restore with range guards); no behavior change to existing paths.
- `godot/src/AI/AiOpponentSystem.cs` / `Core/ScenarioDirector.cs` / `Dsl/DslVarTable.cs` / `Core/MatchStats.cs` — capture/restore of per-match runtime state.
- `godot/src/UI/InMatchMenuOverlay.cs` — real Save/Load buttons + `OnSave`/`OnLoad` + `ChimeraDialog` slot picker with per-slot metadata; `!online` gate.
- `godot/src/Core/Bootstrap/Phases/GameOverOverlayPhase.cs` / `SceneContext.cs` — construct + `_ctx`-store `LocalSaveStore` via `GlobalizePath("user://saves/")`.
- `godot/src/Core/MainScene.cs` — `IssueSave`/`IssueLoad` (capture→write / read+validate→reload), autosave accumulator in the offline `_Process` branch, overlay dismiss + accumulator reset on return-to-Edit, `RestoreInto` as the last step of `ResetToAuthoredStart`, HUD save/load notice for surfaced failures.
- `godot/ProjectChimera.Sim.Tests/Persistence/SaveLoadTests.cs` — NEW: 20 tests (byte-identical resume over golden + with live modifier/persistent + recycled slots + hero/research/projectile/DSL + fired run_once trigger + long-stable modifier; MatchStats round-trip; format stability; fail-closed for bad magic/older+newer version/content-hash/unknown-section/truncation/flipped-body/count-over-cap/short-lane/out-of-range-modifier-host; algo-version pins).

**Review findings breakdown:** 11 patched (2 high / 8 medium / 1 low — all applied and re-verified, incl. the in-engine ordering fix), 3 deferred (cold-boot load-from-menu, AI cross-machine portability, sync-serialization perf → `deferred-work.md`), 0 rejected.

**Follow-up review recommendation:** `true` (2 patched findings were high severity; also 3×8 + 1×1 = 25 ≥ 5).

**Verification performed (independently re-run by the orchestrator):** `dotnet build godot/godot.csproj` → Build succeeded (0 errors). `dotnet test …/ProjectChimera.Sim.Tests -c Release` → 20/20 persistence tests pass within a green suite (full suite reported 3613 passed / 0 failed / 1 pre-existing skip, +20 persistence tests total). `git status` shows no golden/`SimChecksum` file changed; hash `AlgoVersion` pins intact (SimChecksum=21, CanonicalModelHash=14, StartStateHash=2). In-engine gate: **PASS** (see block above).

**Residual risks:** cold-boot load-from-main-menu is deferred (the header persists the launch record for it); saves are same-machine in 1.0 (AI float determinism, deferred); serialization runs on the game thread (perf hitch possible at high entity counts, deferred). The body-hash header field was added under `FormatVersion` 1 rather than a version bump — benign pre-release (old saves fail closed) but a same-version format change relies on the fail-closed path; a bump to v2 would be cleaner if any saves ship.
