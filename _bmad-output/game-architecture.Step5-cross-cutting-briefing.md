# Step 5 Cross-Cutting Concerns — Briefing

> Companion to the **Cross-Cutting Concerns (Step 5)** section in `game-architecture.md` (the canonical
> record). This sidecar carries the deeper reasoning, code anchors, and the manual adversarial review.
> **Note on method:** the planned 11-agent research+verify workflow was interrupted by a usage cap, so this
> was authored directly from the Step 1–4 grounding and a by-hand application of the three review lenses
> (determinism-soundness · scope/sequencing · completeness). Decisions were presented recommend-and-confirm;
> **Alec's confirmed calls are tagged ✅**. Decided 2026-06-20.

## Why testing is the headline (the foundation premise)
The repo has **zero game tests** (`tests/` empty; the only Python tests are unrelated bmad tooling). M1
"foundation trust" (PRD) = the automated suite (FR-44/47) + smoke the 4 unverified systems (FR-45) + pass the
LAN determinism test (FR-39, the #1 ship risk). **Every** D1/D2/D3 migration step is "golden-checksum-gated"
against a harness that does not exist yet, and D1 step 1 is literally "stand up the repo's first headless
deterministic tests." Therefore the test/determinism/CI architecture is the **first thing built** and the
regression guard for the whole migration program — including the cross-cutting hashing fix (SimChecksum
P1/P2-only; pure-relay server; stale AI hash) that D4+D5+D6 converged on.

## 1. Two-tier testing ✅ — the central decision
**Decision (recommended, confirmed): two-tier.** The sim is pure Godot-free C# (`godot/CLAUDE.md` mandate;
verified — no `using Godot` in `src/Core`/`Combat`/`Economy`/`Navigation`). That makes the rules testable
*without launching the engine*.
- **Tier 1 — Godot-free test project** (`ProjectChimera.Sim.Tests`, xUnit or NUnit): determinism + rules +
  golden-checksum + negative-validation + perf-throughput + secret-exclusion. Seconds per run; CI-friendly.
- **Tier 2 — GdUnit4 in `godot/tests/`** (FR-44): presentation/Godot-API/integration only.

**Why not GdUnit4-only (the literal FR-44 reading):** GdUnit4 runs *inside* Godot — every run boots the
engine. Routing the determinism/rules suite through it makes the loop slow and, crucially, **cannot be reused
by the dedicated server**. The two-tier split has a structural payoff: the Tier-1 Godot-free project is the
**same shared-source project the deferred D5 AOT server compiles from** (D3/D5 established AOT needs a
Godot-SDK-free `.csproj` sharing the sim source). One project, two consumers (tests + server). **Action:**
update FR-44's wording from "GdUnit4 only" to the two-tier reality; FR-44 predates the Godot-free/AOT insight.

**Golden-checksum harness shape:** load a fixed scenario → run N ticks through `SimulationLoop` → capture the
`SimChecksum` per tick → assert the sequence equals a committed golden file; then feed the recorded `.chmr`
through `ReplayPlayer` and assert an identical sequence (reuses `ReplayRecorder`/`ReplayPlayer`). Fixtures
(golden scenarios + recorded replays) are committed. The harness itself must be deterministic — **no
wall-clock seed** (the `StressTest.Randomize()` anti-pattern is banned), seed from a fixed match seed.

## 2. Determinism enforcement & cross-platform ✅
Today determinism is discipline-only and already breached: `StressTest.cs:35` wall-clock RNG; `SimChecksum.cs:53-54`
P1/P2-only; `ScenarioDirector` in-tick float/"F2"/TryParse (A17, :168/170/252), unstable `Array.Sort` (:192),
`Dictionary` enumeration (:149).
- **Banned-API analyzer** (Roslyn/`BannedApiAnalyzers`) over the sim layer: forbids `using Godot`, `float`
  gameplay math, `System.Random`/Godot RNG, `DateTime`/wall-clock, nondeterministic enumeration. Build fails
  on violation. Composes with D3's AOT analyzer (same Godot-free source).
- **Cross-platform gate ✅ (Alec: AI-orchestrated runner):** the determinism risk that matters is **Windows
  client vs Linux server** producing different Fixed results. Run the golden-checksum harness on **both** OSes
  and diff the sequences. This is the real FR-39-adjacent proof; same-OS green is necessary but not sufficient.
- Generalize `SimChecksum` to all active factions + Crystal/SupplyUsed/SupplyCap + the new D1/D2/D4 SoA stores,
  with a **guard test** that fails if a per-faction array is added without checksum coverage. Pin
  `InvariantCulture` process-wide.

## 3. Observability & desync diagnosis
- **Deterministic-safe logging seam:** sim logs via an interface (no `GD.Print`, no allocation/ordering
  side-effects), so logging never perturbs the tick and works in the Godot-free project.
- **Bisection:** extend D5's server-side checksum collector with **per-system sub-checksums** (movement /
  combat / economy / DSL / modifier) so a divergence localizes to a system + tick; add **replay-diff** between
  two peers' `.chmr` to find the first diverging tick/entity.
- `.chmr` replay = the canonical reproduce artifact and the opt-in crash/desync report payload.

## 4. Error handling — two worlds
- **Sim:** deterministic, fail-closed, **never throw mid-tick** (Spawn fails closed at 4096 per D1; confirmed
  desync HALTs per D5). An escaped exception is itself a determinism bug.
- **Authoring/presentation:** catch-and-degrade. D3's `Validate(model)` gate rejects bad content pre-tick with
  located errors; FR-34 four-state AI degradation; UGC load failures message, never crash.

## 5. Performance & profiling
- Promote `StressTest.cs` to a repeatable benchmark (deterministic seed, headless variant).
- **CI-gate** headless sim throughput (OS-portable) + **zero-alloc-in-tick** asserts
  (`GC.GetAllocatedBytesForCurrentThread` deltas, enforcing D1's allocate-at-load / ref-struct
  `EffectContext` / work-stack discipline). Render FPS stays periodic in-engine (`godot_profiler`).
- Community-scenario perf fixtures (TD-2000-weak vs 500-brawler profiles differ; combat O(n²) is the hotspot,
  `SpatialHash` mitigates).

## 6. Quality gates & the check-runner ✅
- M1 builds the checks; **the runner is an AI-orchestrated workflow** ✅ (not always-on cloud CI). It runs the
  suites + replays + the Windows↔Linux comparison and reports/diagnoses. Day-to-day Tier-1 runs locally on the
  Windows PC; the cross-platform comparison runs via the workflow against a Linux env when it matters.
- **Advisory, not blocking** on `master` — the repo auto-commits hourly (`[AutoSave]`); a hard pre-commit gate
  would fight that loop. Hard enforcement only on a release branch.
- **Linux env prerequisite (implementation-time):** **Alec already has WSL/Ubuntu installed** (from his bmad
  automator) and uses its terminal — no OS setup needed. M1 work is small: install .NET in that Ubuntu + run
  the check there + diff against Windows. Same WSL also hosts the Linux dedicated-server build for dev/testing.
  (memory: `linux-env-for-crossplatform-check`).
- The workflow also checks the D3 version stamps move together (`CurrentGameVersion`/`schema_version`/
  `checksum_algo_version`/`PROTOCOL_VERSION`/`min_game_version`).

## 7. Accessibility ✅ + languages ✅
Baseline (PRD §4.11 / decision-log #17): input remap (pure presentation — Input→command-intents), colorblind
palette + mode (blue/red is a confusion pair), UI scale (Theme/content-scale), subtitles — in
`SettingsData`/`SettingsManager`/`SettingsPanel`, with the UX pass. **Languages ✅:** English-first + a
translatable seam for the game's own UI; **UGC text not translated in 1.0**.

## 8. Completeness additions (surfaced by the manual critic pass)
- **Settings schema-versioning:** `SettingsData` grows (D6 provider/model + key-present flag; §7 a11y) — needs
  a `schema_version` + migration like content, or an old settings file breaks a new build.
- **UGC safety is structural:** D1/D2 no-escape-hatch + D3 fail-closed gate **is** the sandbox (no content
  executes as code); mod.io downloads gated by content hashing.
- **Migration & replay-compat tests:** D3 migration registry round-trip tests; replay v1→v2 reject +
  cross-version tests (D5-SD-12/D3.9).
- **Dependency hygiene:** shipped NuGet stays `NakamaClient`; test deps live only in the Godot-free test
  project (sim stays AOT-eligible).

## Telemetry ✅
No analytics in 1.0; dev-only diagnostics + opt-in crash/desync report (`.chmr` + checksum log).

## Adversarial review (applied by hand — the 3 lenses)
- **Determinism-soundness:** the plan catches the 3-way hashing bug only if (a) `SimChecksum` is generalized
  *and* covered by the guard test, and (b) the cross-platform diff actually runs — so both are M1 load-bearing,
  not optional. Caught: the harness must not seed from wall-clock; same-OS-green is insufficient (added the
  Win↔Linux diff as the real gate).
- **Scope/sequencing:** M1 minimum = harness + generalized checksum + analyzer + cross-platform diff +
  negative tests; everything else (8-peer soak, full a11y, UGC localization, analytics) is honestly deferred.
  Caught: don't hard-block `master` (fights `[AutoSave]`); the AI-runner must have a scheduled trigger or it
  silently never runs before releases (added as a residual risk).
- **Completeness:** added settings-versioning, UGC-safety-is-structural, migration/replay-compat tests,
  dependency hygiene; reconciled the two-tier split with FR-44 (update wording, GdUnit4 stays the integration
  tier — not a contradiction).

## Decisions confirmed by Alec
1. Two-tier checks (fast Godot-free rule checks + GdUnit4 in-game checks). ✅
2. Check-runner = an AI-orchestrated workflow + a Linux env for the cross-platform comparison. ✅
3. Telemetry = dev-only + opt-in crash/desync report (no analytics). ✅
4. Languages = English now + translatable seam for own UI; skip UGC translation. ✅

## M1 foundation build sequence
1. Godot-free test project + golden-checksum replay harness. 2. Generalize `SimChecksum` + coverage guard.
3. Determinism banned-API analyzer (+ D3 AOT analyzer). 4. AI check-runner + Win↔Linux comparison (Linux env).
5. Negative-validation tests + perf benchmark + zero-alloc asserts. → then the D1→D2→D3 strangler.

## Hand-offs
→ M1 implementation (the gds-test-framework/-automate/-performance skills implement this). → Step 6
(`MainScene` decomposition must become testable as it splits). → the deferred D5 AOT server shares the
Godot-free project's source.

## Residual risks
Cross-platform identity is asserted until the Linux env is wired in (FR-39 = #1 risk); the AI-runner must be
reliable/scheduled enough to actually run before releases; zero-alloc asserts are refactor-brittle (treat
regressions as real); accessibility is a baseline, not full WCAG.
