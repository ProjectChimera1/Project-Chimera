# Step 6 — Project Structure + `MainScene` Decomposition — Briefing

> Companion to the **Step 6 — Project Structure + `MainScene` Decomposition** section in
> `game-architecture.md` (the canonical record). This sidecar carries the deeper reasoning: the method, the
> three candidate strategies + the adversarial scoreboard, the reviewer-found flaws that were eliminated, the
> hard-constraint rubric, and the full text of the four scope decisions with Alec's confirmed calls.
> Decided 2026-06-20.

## Method
Produced via a **16-agent design + adversarial-verify workflow** (token budget unconstrained — ultracode):
1. **Ground (3 agents):** verified a grounding brief against the actual code — a full read of
   `godot/src/Core/MainScene.cs` (2,223 LOC), the D1–D6 + Step-5 hand-offs in `game-architecture.md`, and the
   as-built directory conventions (`godot/CLAUDE.md`, `architecture.md`). Corrections/additions fed the panel.
2. **Design (3 agents, diverse lenses):** testability-first; brownfield-minimal-risk; sim/presentation-boundary-
   purist. Each produced a complete structure + decomposition + strangler sequence + C1–C8 coverage.
3. **Verify (9 agents):** each design scored by 3 judges — adversarial breakage hunter, constraint auditor,
   solo-dev/strangler realist.
4. **Synthesize (1 agent):** top-ranked spine + grafts from the runner-ups, every fatal flaw eliminated.

## The hard-constraint rubric (C1–C8)
- **C1** Preserve the fragile `_Ready()` ordering (or make it explicit + safer; never silently reorder).
- **C2** Create the Godot-free, fail-closed `Validate(model)` gate at the `ApplyScenario` boundary; the
  scenario→sim apply path becomes pure-C#, testable without Godot, reusable by the headless server.
- **C3** Respect + repair the sim/presentation boundary (move sim-mutation to the sim layer behind a clean
  command/builder API; preserve the `On*` delegate seam).
- **C4** Strangler-compatible: incremental, golden-checksum-gated, always-shippable, never fights the hourly
  `[AutoSave]`→master loop.
- **C5** Every net-new D1–D6 + Step-5 module gets a definitive home; a thin composition root they plug into
  without regrowing the god object.
- **C6** Dedicated-server entry is a clean boundary sharing the Godot-free sim/apply path (aligns with the
  deferred AOT `.csproj` split without forcing it now).
- **C7** Generalize/isolate the hardcoded 2-faction HUD/win/game-over assumptions so D5 N≤8 is localized.
- **C8** Solo-dev pragmatic: minimal ceremony, no heavyweight DI unless justified; matches the manual-`new` idiom.

## The three candidates + scoreboard
| Rank | Strategy | Avg score |
|---|---|---|
| 1 (chosen spine) | **Shrinking Composition Root + Coordinator Strangler** (Godot-free SimBuilder seam first) | **93** |
| 2 (grafted) | Thin Composition Root + SimWorld Sandwich (boundary-purist / forward-fit) | 92 |
| 3 (grafted) | Sim Spine First — extract a Godot-free `SimulationHost` (Builder + ScenarioApplier + Validate gate) both client and server compose | 89 |

**Why A won, and what was grafted.** A is the only design whose **first commit takes zero irreversible risk**
against the hourly `[AutoSave]`→master loop (C4). The two runner-ups each had a single disqualifying flaw the
reviewers caught:
- **B** ended in a `main.tscn:3` root-script repath bundled with five extractions — a multi-hour red window if
  AutoSave fires mid-edit. *(A keeps MainScene in place; no scene-repath cutover.)*
- **C** asserted `ApplyScenario` moves "verbatim, Godot-free" while it still embeds
  `ProjectSettings.GlobalizePath` (MainScene.cs:509) + `FactionDefinition.LoadFromFile` (MainScene.cs:512) — a
  build-break on master. *(A inserts a path-resolution pre-pass so the Godot-free claim is true the instant the
  applier compiles.)*

**Grafts that made the synthesis stronger than any single design:**
- From **B**: `ISetupPhase[]`-as-data + `PhaseOrderTest` (cleanest C1 — order becomes asserted data, not implicit
  method-call timing); the `SimWorld` aggregate (one container server + Sim.Tests both build).
- From **C**: `Validated<ScenarioModel>` (a *type* that makes "no raw `Fixed.FromFloat` on external data outside
  the gate" analyzer-checkable, not a convention); the single `SetChecksumSink` owner; `FactionSlots.ToFaction`.

## Fatal flaws the reviewers found — and how the final design eliminates each
All three reviewers independently re-derived the same correction set (high confidence these are real):
1. **Missing `Validate(model)` gate** — `ApplyScenario` does zero validation; `slot.StartOre` trusted verbatim
   at MainScene.cs:518. → `ScenarioValidator` + `Validated<ScenarioModel>` (sub-decision 4).
2. **Mid-tick presentation write** — `ScenarioDirector.OnSpawnUnit` is a presentation closure calling
   `SpawnScenarioUnit` *inside* the sim step (director runs last). → re-point at `ScenarioApplier.SpawnUnit`
   (sim→sim) (sub-decision 7).
3. **`(Faction)(slot+1)` `+1` duplicated** at MainScene.cs:504/534/543 + the OnSpawnUnit closure → `FactionSlots`.
4. **Hidden `_uiCanvas` dependency** — ≥5 later phases `AddChild` to it; reordering = silent NRE. → `HudPhase`
   owns it, injected as a ctor dep (compile/null-checked) (sub-decision 6).
5. **OnChecksum double-set** — set inline at MainScene.cs:269 then overwritten at MainScene.cs:1761 (MP). →
   single `SetChecksumSink` owner (sub-decision 2 / migration Step 10).
6. **`[5]`/`[2]` faction-array hardcodes** (`new FactionDefinition[5]` :242, `StartPositionBridge[2]` :965) →
   `FactionRegistry` (sub-decision 8).
7. **The server has NO shared sim path today** (holds zero sim state) — "preserve the shared path" was false;
   `ServerBootstrap` **creates** it (sub-decision 5).
8. **Un-homed `ILogSink` + the second `using Godot;` file in `src/Core` (`StressTest.cs`)** → `ILogSink` seam
   (sub-decision 9); relocate `StressTest.cs` to `tools/` (sub-decision 11).
9. **The sim-side 2-faction hardcode the runner-ups left un-homed** — `ScenarioDirector`'s
   `OnVictory?.Invoke(1 - a.Faction)` (ScenarioDirector.cs:326) + `slot < 2` loop (ScenarioDirector.cs:165). →
   rewritten to iterate active factions via `FactionRegistry` (sub-decision 8 / migration Step 11).

## The four scope decisions — full text + Alec's confirmed calls

### 1. Validation-gate flip timing → ✅ **Warn-first on master, fail-closed on a release branch**
*Question:* When does the `Validate(model)` gate flip from log-only (shadow) to fail-closed (reject→fallback)?
- **Chosen — Release-branch flip after corpus-verify:** land log-only on master; flip to fail-closed only on a
  release branch after a corpus run proves every shipped `resources/data` scenario (+ fallback + AI-gen paths)
  passes. **C2 reported satisfied only post-flip, never at land-time.** Lowest risk to the AutoSave loop; matches
  the canonical advisory-gate posture.
- *Rejected — Flip on master once negative tests green:* closes C2 sooner but a too-tight bound can break a
  tolerated map between two hourly commits.

### 2. Sim-core extraction scope → ✅ **M1-blocking (the spine)**
*Question:* Is the Godot-free sim-core (`SimBuilder`/`ScenarioApplier`/`Validate` seam) + `ServerBootstrap`
M1-blocking or opportunistic?
- **Chosen — M1-blocking:** `SimulationHost` + `SimBuilder` + `ScenarioValidator` + `ServerBootstrap` are the M1
  foundation that must land before D3/D4/D5/D6 content reaches the lobby; the golden harness, negative-validation,
  the canonical start-state hash, and the server-shares-sim-path proof all ride this seam, and the cross-cutting
  3-way hashing bug is only fixable once it exists.
- *Rejected — Opportunistic/partial:* leaves C6 unproven and the server holding zero sim state longer.

### 3. AOT server `.csproj` pre-split → ✅ **Keep deferred; discipline-only now**
*Question:* Pre-split the NativeAOT server `.csproj` now, or keep it deferred (per D5)?
- **Chosen — Keep deferred:** land only (a) the Godot-free shared-source set glob-included into
  `ProjectChimera.Sim.Tests`, (b) the `BannedSimApiAnalyzer` + AOT-eligibility analyzer (advisory), (c)
  `ServerBootstrap` structured so its include set is the future AOT manifest, (d) `EnableDynamicLoading` never
  inherited by that future project. Matches D5's explicit deferral; ship/verify N≤4 in 1.0.
- *Rejected — Pre-split + PublishAot now:* a second maintained build target through 1.0 for a post-1.0 payoff.
- *Rejected — Pre-split structure, no PublishAot:* middle ground; the Sim.Tests project already forces the
  Godot-free compile, so a second `.csproj` buys little now.

### 4. D6 secret/config migration timing → ✅ **Land with the editor/MP coordinator carve (migration Step 12)**
*Question:* When does ripping `[Export] AnthropicApiKey`/`ModIoApiKey` out to `ISecretStore` + moving
provider/model into versioned `SettingsData` land?
- **Chosen — With the coordinator carve:** the `LLMService`/`ModIoService` key sites are inside the exact
  coordinators (`EditorToolsController`/`MatchLifecycleController`) being extracted; fold `ISecretStore` + the
  `SecretExclusionTest` in then. One coherent presentation-layer pass; retires the plaintext-`[Export]` smell as
  the god object shrinks.
- *Rejected — Defer to a standalone late step:* cleaner isolation of settings-versioning but leaves the
  plaintext-key smell live longer and re-touches the same coordinators twice.

## Open items (non-blocking; carry forward)
- `StressTest.cs` relocation target (`tools/` vs `tests/benchmark`) + whether `scenes/stress_test.tscn`'s
  script-path edit is an isolated commit or an analyzer suppression. *(Implementation-time; Step 0/11.)*
- The deferred Linux/WSL cross-platform build check runs against `ProjectChimera.Sim.Tests` at M1 (only
  .NET-in-WSL is missing — Alec already has WSL/Ubuntu; memory `linux-env-for-crossplatform-check`).
- Per-system sub-checksums + `.chmr` replay-diff desync tool (D5 diagnosis): homed structurally, **fast-follow**,
  not 1.0-blocking unless an actual N-player desync forces them earlier.
- Translatable-UI + accessibility-baseline content rides the D6/settings band (migration Step 12) or a later
  cross-cutting pass.

## Hand-offs
→ **Step 7 (Implementation Patterns)** lifts the read-rail/write-rail, `ISetupPhase`, `ScenarioDelegateBinder`,
single-owner `SetChecksumSink`, and `Validated<T>` patterns as canonical. → **M1 implementation** builds
migration Steps 0–6 as the foundation (the gds-test-framework/-automate/-performance skills implement the harness
this rides). → The deferred **D5 AOT server** shares `ProjectChimera.Sim.Tests`' Godot-free source set.

## Residual risks
Coordinator extraction (Step 8) is the one band a red client build can slip past the golden gate (sim untouched →
a missed `_uiCanvas`/`_camCtrl` ref is a runtime NRE, not a checksum mismatch) — one phase per commit + ctor-dep
injection + mandatory in-engine smoke. C2 is only truly enforcing after the release-branch flip. The flip can
reject a previously-tolerated map (corpus-verify + legacy-amnesty first). `SimChecksum` widening (Step 13) is a
hash-changing re-baseline. N-faction verified only at N≤2 today (Step 11 adds a 3–4-faction golden scenario).
Shared-source `<Compile Include>` drift could hide a Godot leak until the AOT step (glob-includes + CI folder-set
match check).
