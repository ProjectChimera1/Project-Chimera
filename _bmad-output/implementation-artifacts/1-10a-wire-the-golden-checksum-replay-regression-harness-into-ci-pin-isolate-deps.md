---
baseline_commit: 6b20883
---

# Story 1.10a: Wire the golden-checksum replay regression harness into CI + pin/isolate deps

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- 1.10a is an INFRASTRUCTURE story, not a sim/gameplay story. It writes ZERO sim code. It takes the
     already-built, already-green Tier-1 golden-checksum harness (1.2 → algo v3) and makes it run automatically
     in CI on every push, failing the build on any determinism regression; locks the dependency surface
     (NakamaClient 3.13.0 pinned; test-only deps isolated) with a permanent guard test; and documents the
     AR-41 telemetry posture folded into this story. The big enablers are already true: the test project is
     Godot-free (plain Microsoft.NET.Sdk) so CI needs ONLY the .NET 8 SDK — no Godot install — and the dep
     pin/isolation already exists in the .csproj files; 1.10a GATES and LOCKS what is already there. -->

## Story

As a solo developer who must keep determinism from silently breaking,
I want the golden-checksum replay regression harness from Story 1.2 (now at the current `SimChecksum` algo version) **wired into CI so every push runs the golden scenarios headless and fails the build on any checksum diff**, with **NakamaClient pinned at 3.13.0** and **test-only deps kept isolated to the Godot-free test project** (and locked behind a regression guard so they can't drift),
so that any future change that breaks determinism fails CI **before it can ship**, and the dependency surface stays **reproducible**.

> **This is the regression-guard keystone that closes the loop on the whole Epic-1 determinism spine.** Stories 1.2–1.9 built and proved the determinism machinery (golden harness, generalized `SimChecksum`, `Fixed` end-to-end, `SimRng`, `ScenarioValidator`, the Godot-free `SimulationHost`/`ServerBootstrap`, and the LAN/server quorum). All of it is currently verified **only when someone runs `dotnet test` by hand.** 1.10a makes the golden gate run **automatically on every push** so 1.9's hard-won LAN-green can never regress silently. It also **establishes the headless CI lane** that 1.10b's analyzers and 1.10c's cross-platform Windows↔Linux gate plug into next — so getting the lane's shape right matters beyond this story.
>
> **Three facts shape this story and are settled below:** (1) The Tier-1 test project (`ProjectChimera.Sim.Tests`) is **Godot-free** — it uses `Microsoft.NET.Sdk` (not `Godot.NET.Sdk`) and compiles the pure-sim source directly via `<Compile Include>`. So CI is a plain `dotnet test` against **that one `.csproj`** with **only the .NET 8 SDK installed — no Godot, no editor, no GodotSharp.** (2) The **dependency pin and isolation already exist**: `godot/godot.csproj` already pins `NakamaClient 3.13.0` as its *only* `PackageReference`, and the test deps (xUnit, test SDK) already live *only* in the test `.csproj`. 1.10a's job for AC2 is therefore to **lock that in with a guard test**, not to change it. (3) There **is** a GitHub remote (`ProjectChimera1/Project-Chimera`), so **GitHub Actions is the CI home** — and it is **greenfield** (zero workflow files exist today).

## Acceptance Criteria

1. **(CI runs the golden gate headless on every push and fails on any checksum diff — the regression guard)** **Given** the Tier-1 golden-checksum replay harness from 1.2 (now at `SimChecksum.AlgoVersion = 3`), **When** CI runs on a push, **Then** a GitHub Actions workflow executes `dotnet test` against **`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`** headless and the job **FAILS** (non-zero exit) on **any** checksum diff against any committed `*.golden.txt`. The run installs **only the .NET 8 SDK** (no Godot), and **`CHIMERA_GOLDEN_RECORD` is never set** in CI (so the harness runs in verify mode, asserting against the committed goldens, never re-recording them). The workflow triggers on `push` and `pull_request` (and supports manual `workflow_dispatch`).

2. **(Dependency pin + isolation are locked behind a permanent guard test)** **Given** the project's dependency surface, **When** the Tier-1 suite runs (locally or in CI), **Then** a new guard test asserts: (a) `godot/godot.csproj` pins **`NakamaClient` to exactly `3.13.0`** and contains **no** test-only `PackageReference` (no `xunit*`, no `Microsoft.NET.Test.Sdk`); and (b) the test-only deps live **only** in `ProjectChimera.Sim.Tests.csproj`. The guard fails if the pin version drifts or any test dependency leaks into the shipping game project. (Both csproj files already satisfy this today — the test makes it **permanent**, matching the existing `SimChecksumCoverageGuardTest`/`GodotFreeBoundaryTest` guard-test pattern.)

3. **(No regression — goldens byte-identical, CI stays Godot-free, suite green)** **Given** the full Tier-1 suite, **When** the exact CI command runs, **Then** all **four** existing `*.golden.txt` verify green **unchanged** (`git status` clean for goldens), the whole suite is green, and **no sim code, `SimChecksum`, tick, or wire format changed** — every change in this story is **additive CI/config/test/docs**. CI must **not** reference `godot.sln` or `godot.csproj` or install the Godot SDK (Tier-1 is Godot-free by design; pulling Godot in would defeat the AOT-eligibility intent of AR-2/AR-35).

4. **(AR-41 telemetry posture documented — folded into this story)** **Given** the 1.0 telemetry posture, **When** a contributor looks for the analytics policy, **Then** a short committed doc states: **NO analytics/telemetry collection in 1.0**; dev-only diagnostics are the existing **logs (`ILogSink`) + `.chmr` replay capture** (`ReplayRecorder`/`ReplayPlayer`); and an **opt-in crash/desync report** that bundles the `.chmr` + checksum log is a documented **fast-follow, explicitly NOT built in 1.0**. No analytics pipeline, SDK, or network beacon is added.

_Covers: **FR-47** (CI regression guard fails the build on any determinism/checksum diff), **FR-44** (two-tier deterministic test infrastructure, now CI-enforced), **AR-2** (NakamaClient 3.13.0 pin + test-only deps isolated to keep the sim AOT-eligible), **AR-35** (Tier-1 Godot-free xUnit project is the CI home), **AR-41** (no-analytics telemetry posture, folded into 1.10a per `epics.md:492`), **AR-47** (the regression harness wired into CI). Depends on: 1.9b (DONE — the LAN-green this guard protects). Independent of 1.10b (analyzers / AR-36) and 1.10c (cross-platform Windows↔Linux gate / AR-37), which plug into the lane this story establishes._

> Establishes the headless CI lane. Brownfield: the harness, the dep pin, and the dep isolation **already exist and are green** — 1.10a automates and locks them. **Additive only — the 30 Hz tick, `SimChecksum`, the goldens, and the wire are untouched.** Getting the lane's *shape* right (Godot-free, targets the test `.csproj`, fails hard on a golden diff) is the real value, because 1.10b and 1.10c build on it.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** This story is **almost entirely YAML + config + one guard test + one doc.** It writes **zero sim code**. The substance is: (1) a GitHub Actions workflow that runs the existing Tier-1 golden gate headless and fails on a determinism regression; (2) a Tier-1 guard test that locks the NakamaClient pin + test-dep isolation; (3) optional-but-recommended reproducibility hardening (SDK pin via `global.json`, package lock file); (4) a short AR-41 telemetry-posture doc. The hard part is **not** the code — it is **respecting the scope fences** so you don't accidentally do 1.10b's analyzers, 1.10c's Linux leg, or build an analytics system. The traps:

1. **Dragging Godot into CI.** The Tier-1 project is **Godot-free** (`Microsoft.NET.Sdk`, compiles `..\src\Core|Combat|Economy|Navigation|AI|…` directly). CI must target **`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` directly** and install **only the .NET 8 SDK**. Do **NOT** target `godot/godot.sln` or `godot/godot.csproj`, and do **NOT** install Godot / the editor / GodotSharp — they need `Godot.NET.Sdk` and would make CI slow, heavy, and architecturally wrong (the whole point of the Godot-free Tier-1 split, AR-2/AR-35, is that the determinism gate runs without the engine). (D2)
2. **Setting `CHIMERA_GOLDEN_RECORD` in CI.** That env var (`GoldenChecksumReplay.RecordEnvVar`) flips the harness into **re-baseline mode**, where the AC tests **early-return** and the golden files are **rewritten**. If it is ever set in CI, the gate proves **nothing** and would silently overwrite the goldens. **Never set it in the workflow.** (AC1)
3. **"Fixing" a red CI by re-recording a golden.** If CI goes red on a checksum diff, that is the gate **working** — a real determinism regression. **Find and fix the code**, never `git`-edit a `*.golden.txt` or run the record path to make it green. The only legitimate re-baseline is an *intentional* sim-behavior change (its own deliberate commit), which is **not** part of 1.10a. (AC3/Trap-class)
4. **Hardcoding a test count in CI.** The suite is ~191–192 tests today and grows every story. Do **NOT** assert "expect 192 tests" anywhere — assert the **job exit code is 0** (xUnit returns non-zero on any failure). A hardcoded count makes CI red on every new test. (AC1)
5. **Building 1.10c's cross-platform Linux leg.** Running the gate on **Linux/WSL** and **diffing** Windows↔Linux checksum sequences is **Story 1.10c (AR-37)**. For 1.10a, default the runner to **`windows-latest`** (the primary platform; the goldens were recorded on Windows). Do not add a Linux job or a cross-OS diff here. (D3/Scope fence)
6. **Pulling 1.10b's analyzers forward.** The banned-API + AOT-eligibility **Roslyn analyzers** (advisory on master / enforce on release) are **Story 1.10b (AR-36)**. 1.10a only stands up the **lane**; do not add analyzers. (Scope fence)
7. **Building an analytics/telemetry system for AR-41.** AR-41 is **NO analytics in 1.0.** Your deliverable is a **posture doc**, not code. The opt-in crash/desync **bundler** is an explicit **fast-follow** (`epics.md:234,492`), not 1.0 work — do not build it, do not add a telemetry SDK or network beacon. (D7/Trap #7)
8. **Over-pinning the SDK so local builds break.** If you add a `global.json`, pin to the **`8.0` family with `rollForward`** (e.g. `latestFeature`) — never an exact patch with no roll-forward, or a machine that lacks that exact patch can't build. (D6)
9. **Touching the dependency pin "to be safe."** `godot.csproj` **already** pins `NakamaClient 3.13.0` correctly and has no test deps; the test `.csproj` **already** isolates xUnit. AC2 is a **guard test that asserts the status quo**, not an edit to the csproj. Do not bump, re-order, or "tidy" the pins. (D5/AC2)

### The shape of the work (1 workflow + 1 guard test + reproducibility hardening + 1 posture doc; then prove no-regression. ZERO sim code. Goldens UNCHANGED.)

1. **GitHub Actions workflow** (`.github/workflows/determinism-gate.yml`, net-new) — `on: push` (all branches) + `pull_request` + `workflow_dispatch`; `concurrency` with `cancel-in-progress` (collapses the hourly `[AutoSave]` push bursts to one run); `runs-on: windows-latest`; steps: `actions/checkout@v4` → `actions/setup-dotnet@v4` (`8.0.x`) → `dotnet restore` the **test csproj** → `dotnet test` the **test csproj** in Release. `CHIMERA_GOLDEN_RECORD` unset. (AC1) — **the core deliverable.**
2. **Dependency-hygiene guard test** (`ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs`, net-new Tier-1) — parses `../godot.csproj` and the test `.csproj` (located portably via `[CallerFilePath]`, the same pattern `GoldenChecksumReplay.GoldenSourcePath` uses); asserts the NakamaClient 3.13.0 pin + zero test deps in `godot.csproj`. (AC2)
3. **Reproducibility hardening** (recommended) — `global.json` pinning the SDK to `8.0` (`rollForward: latestFeature`) for local/CI parity; optionally a `packages.lock.json` on the test project (`RestorePackagesWithLockFile`) + `--locked-mode` restore in CI for a fully reproducible package graph; optionally harden `.gitignore` for the nested test `bin/obj`. (AC2/AC3)
4. **AR-41 telemetry-posture doc** (`docs/telemetry-posture.md`, net-new) — NO analytics in 1.0; dev diagnostics = logs + `.chmr`; opt-in crash/desync bundle = fast-follow, not built. (AC4)
5. **Prove AC3** — run the exact CI command locally with **no** record env var → all green; four goldens byte-identical; confirm the workflow installs no Godot. Push a branch and confirm the Actions run is green (or note that the first push validates the lane). (AC3)

### Key design decisions (settled here — do NOT re-derive)

**D1 — CI = GitHub Actions, under `.github/workflows/`; greenfield.** The repo has a real remote — `git@github-chimera:ProjectChimera1/Project-Chimera.git` (the `github-chimera` host is a local `~/.ssh/config` alias; GitHub Actions runs server-side and is unaffected). There are **zero** existing workflow/CI files (verified: no `.github/`, no `azure-pipelines`, no `.gitlab-ci`). So this is a clean GitHub Actions setup. The workflow file lands at **`.github/workflows/determinism-gate.yml`** (it is the only new git-tracked artifact besides the guard test, `global.json`, and the doc).

**D2 — CI targets the test `.csproj` directly and installs ONLY the .NET 8 SDK — CI is Godot-free.** `ProjectChimera.Sim.Tests.csproj` is `Microsoft.NET.Sdk` (NOT `Godot.NET.Sdk`) and pulls the sim source in via `<Compile Include="..\src\Core\**\*.cs" />` (+ Combat/Economy/Navigation/AI/selected Multiplayer + Server), with `MainScene.cs` and `Bootstrap/Phases/**` removed. `FixedPoint.cs`'s `#if GODOT` bridge compiles out (the `GODOT` symbol is undefined here). Net result: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` is **fully self-contained** — it needs no `ProjectReference` to `godot.csproj`, no GodotSharp, no editor. **CI therefore needs only `actions/setup-dotnet` with `8.0.x`.** Do not point CI at `godot.sln`/`godot.csproj` (they require `Godot.NET.Sdk/4.6.3`). This is the single most important shape decision — it is what makes the determinism gate cheap and keeps the sim AOT-eligible (AR-2/AR-35).

**D3 — Runner = `windows-latest` for 1.10a; the Linux leg is 1.10c.** The as-built game is Windows-primary and the four goldens were recorded on Windows. Running the gate on Windows keeps 1.10a a clean **"regression guard on the home platform."** Diffing Windows↔Linux checksum sequences is **explicitly AR-37 / Story 1.10c** (via the existing WSL/Ubuntu). The harness is deliberately portable (embedded-resource goldens, `InvariantCulture` parsing, `'\n'`, integer `Fixed` math) so 1.10c can reuse it — but **do not** add the Linux leg here. _(Cost note: `windows-latest` minutes bill at 2× on GitHub-hosted runners. `ubuntu-latest` is cheaper and, if the goldens pass there, would incidentally demonstrate cross-platform parity — but that is 1.10c's job and a red ubuntu run would pull you into cross-platform debugging inside a "just wire CI" story. **If you choose ubuntu and it is green, fine; if it is red, STOP — that is a 1.10c finding, not a 1.10a expansion.** Default stays Windows. Open Question #1.)_

**D4 — Trigger on every push (all branches) + PR + manual, with `concurrency: cancel-in-progress`.** AC1 says "on every push," so `on: push` with `branches: ['**']` + `pull_request` + `workflow_dispatch`. The real concern is the project's **hourly `[AutoSave]` commit loop** (~24 pushes/day) multiplying CI minutes. The highest-leverage, lowest-risk mitigation is a **`concurrency` group keyed by `github.ref` with `cancel-in-progress: true`** — a burst of autosave pushes collapses to a single run. CI going red does **not** block the autosave loop (the commit already happened locally and pushed; CI just reports the signal) — which is exactly what you want a determinism regression to do. _(If minutes become a problem, the levers are: switch to `ubuntu-latest` (D3), restrict to `push` on `master` + `pull_request`, add a `paths-ignore` for pure-doc commits, or skip `[AutoSave]`-titled commits. Pick a default of all-branches + concurrency-cancel; Open Question #2.)_

**D5 — AC2 is discharged as a permanent Tier-1 guard test, not a one-time CI grep.** The project's culture is "assert invariants as Tier-1 tests" — `SimChecksumCoverageGuardTest`, `GodotFreeBoundaryTest`, `PhaseOrderTest`, `SystemOrderTest`. Follow it: a new `DependencyHygieneTests` reads the two `.csproj` files as text/XML and asserts the pin + isolation. Locate the csprojs **portably** via `[CallerFilePath]` (resolve the test source dir, then navigate to `godot/godot.csproj` and the sibling test csproj) — the **same** mechanism `GoldenChecksumReplay.GoldenSourcePath` already uses, so it works locally and on the CI checkout without hardcoded absolute paths. This makes the guard run **everywhere the suite runs** (not just in a CI shell step), so a future dep drift fails the developer's local `dotnet test` too. _(Alternative considered + rejected: a `grep`/PowerShell step in the workflow. Rejected because it only runs in CI, not locally, and doesn't match the established guard-test pattern.)_

**D6 — SDK pinning: `actions/setup-dotnet` `8.0.x` in CI; a `global.json` pinning the `8.0` family (recommended) for local/CI parity.** Locally two SDKs are installed (8.0.419 + 9.0.312) and `dotnet` currently resolves to **9.0.312** because nothing pins it (the TFM is `net8.0`, which a 9.0 SDK can still build). For CI, `setup-dotnet` with `dotnet-version: 8.0.x` is sufficient. Adding a repo-root **`global.json`** (`"sdk": { "version": "8.0.419", "rollForward": "latestFeature" }`) makes local and CI use the same SDK family — good reproducibility hygiene. **Determinism itself is integer `Fixed` math and is SDK-version-independent**, so this is reproducibility polish, not a correctness gate — keep the `rollForward` so a missing exact patch never bricks a build. _(Open Question #3.)_

**D7 — AR-41 is a posture doc, not code.** `epics.md:492` folds AR-41 into 1.10a; `epics.md:234` says "**NO analytics in 1.0.** Dev-only diagnostics (logs, `.chmr` replay capture) + an opt-in crash/desync report. Desync-diagnosis tooling (per-system sub-checksums, replay-diff) is homed structurally as a fast-follow, **not 1.0-blocking**." The `.chmr` replay capture already exists (`ReplayRecorder.cs`/`ReplayPlayer.cs`, compiled into Tier-1). So the discharge is a **short committed doc** stating the posture and that the bundler is a fast-follow. **Build nothing.**

### Pre-flight facts you MUST NOT re-derive (verified against the codebase + environment, 2026-06-24)

**CI is greenfield; the remote exists:**
- **No CI exists.** No `.github/` directory, no tracked workflow/`*.yml` CI files (only `docs/server-deploy/docker-compose.yml`, unrelated — Nakama server deploy). [Source: repo scan; `git ls-files .github` → empty]
- **GitHub remote present.** `origin = ProjectChimera1/Project-Chimera` (push + fetch via the `github-chimera` SSH alias). Default branch `master`; `origin/HEAD → origin/master`. **GitHub Actions is the CI platform.** [Source: `git remote -v`, `git branch -a`]

**The Tier-1 project is Godot-free and self-contained (this is what makes CI cheap):**
- **`ProjectChimera.Sim.Tests.csproj` uses `Microsoft.NET.Sdk`**, `TargetFramework=net8.0`, `ImplicitUsings=disable`, `IsPackable=false`. It compiles `..\src\Core|Combat|Economy|Navigation|AI\**\*.cs` + three Multiplayer files + `..\src\Multiplayer\Server\**` directly; removes `..\src\Core\MainScene.cs` and `..\src\Core\Bootstrap\Phases\**`. **No `ProjectReference` to `godot.csproj`** (deliberate — would drag in GodotSharp). [Source: ProjectChimera.Sim.Tests.csproj:1-47]
- **`dotnet test` on that csproj needs only the .NET 8 SDK** — no Godot. [Source: D2 reasoning above]

**The dependency surface is ALREADY pinned + isolated (AC2 guards it, doesn't change it):**
- **`godot/godot.csproj`** is `Godot.NET.Sdk/4.6.3`, and its **only** `PackageReference` is **`NakamaClient` `3.13.0`**. It also `<Compile Remove>`s `ProjectChimera.Sim.Tests\**\*.cs`. [Source: godot.csproj:1-18]
- **Test-only deps live ONLY in the test csproj**: `Microsoft.NET.Test.Sdk 17.11.1`, `xunit 2.9.2`, `xunit.runner.visualstudio 2.8.2` (the csproj comment already says: "Pin the exact versions `dotnet restore` resolves so CI (Story 1.10a) is reproducible"). [Source: ProjectChimera.Sim.Tests.csproj:68-77]

**The golden harness (the thing CI runs):**
- **Engine:** `GoldenChecksumReplay.cs` — `RunAndRecord`, `CompareSequences` (first divergence), `LoadGolden` (embedded resource), `FormatGolden`/`ParseGolden`, `MaybeRecord` (env-gated). **Re-baseline env var = `CHIMERA_GOLDEN_RECORD` (`RecordEnvVar`); `IsRecordMode` true when it == "1"`.** [Source: GoldenChecksumReplay.cs:25-218]
- **Tests:** `GoldenChecksumReplayTests.cs` — 3 ACs: in-process determinism + golden match; cross-process golden match; one-tick perturbation detected + located. **All three early-return when `IsRecordMode`.** [Source: GoldenChecksumReplayTests.cs:18-134]
- **Four committed goldens (embedded resources):** `golden-scenario.golden.txt`, `golden-multifaction.golden.txt`, `golden-applier-scenario.golden.txt`, `same-tick-tie-break.golden.txt`. Additional golden-backed tests reuse the harness: `MultiFactionGoldenTests`, `GoldenApplierScenarioTests`, `SameTickTieBreakGoldenTests`, `SimRngChecksumReplayTests`. [Source: ProjectChimera.Sim.Tests.csproj:49-66; test-file glob]
- **`SimChecksum.AlgoVersion = 3`** (self-stamped into each golden's `# checksum_algo_version` header by `FormatGolden`). [Source: GoldenChecksumReplay.cs:183]
- **Established guard-test precedent for AC2:** `Golden/SimChecksumCoverageGuardTest.cs`, `GodotFreeBoundaryTest.cs`, `Bootstrap/PhaseOrderTest.cs`, `Sim/SystemOrderTest.cs`. [Source: test-file glob]

**Environment / reproducibility:**
- **Local SDKs:** `8.0.419` and `9.0.312`; `dotnet --version` → **9.0.312** (no `global.json` pins it). **No `global.json` anywhere in the repo.** [Source: `dotnet --list-sdks`; `global.json` glob → none]
- **`.gitignore`** ignores `godot/.godot/`, `godot/.mono/`, `godot/bin/`, `godot/obj/` (each **root-anchored** by the embedded slash). It does **NOT** match the nested **`godot/ProjectChimera.Sim.Tests/bin|obj`** (those are currently **untracked anyway** — `git ls-files` count 0 — but **unprotected**). [Source: .gitignore:1-21; `git ls-files` check]
- **`.chmr` replay capture exists** for AR-41's "dev diagnostics": `ReplayRecorder.cs` + `ReplayPlayer.cs` (compiled into Tier-1). [Source: ProjectChimera.Sim.Tests.csproj:28-29]
- **Previous story (1.9b, DONE)** ran the suite as `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → **191 passing** (debug log), 0 failed, **four goldens byte-identical**, build 0 errors (7 pre-existing CS8632 `#nullable` warnings, out of scope). _(The sprint-status header says "192"; treat the exact count as informational — the gate is GREEN + zero golden diffs, never a magic number.)_ [Source: 1-9b Dev Agent Record]

### Scope fence — do NOT, in this story

- **Do NOT** point CI at `godot.sln`/`godot.csproj` or install Godot/GodotSharp — target the **test `.csproj`**, .NET 8 SDK only (D2/Trap #1).
- **Do NOT** set `CHIMERA_GOLDEN_RECORD` in CI, and **do NOT** re-record/move any `*.golden.txt` (Trap #2/#3/AC3).
- **Do NOT** add a Linux/WSL job or a Windows↔Linux checksum diff — **Story 1.10c (AR-37)** (D3/Trap #5).
- **Do NOT** add banned-API / AOT Roslyn analyzers — **Story 1.10b (AR-36)** (Trap #6).
- **Do NOT** build any analytics/telemetry/crash-bundler code — AR-41 is **posture doc only**; the bundler is a fast-follow (D7/Trap #7).
- **Do NOT** edit the NakamaClient pin or the test-dep set — AC2 **guards** them, the csprojs are already correct (D5/Trap #9).
- **Do NOT** hardcode a test count in CI — assert exit code 0 (Trap #4).
- **Do NOT** change `SimChecksum`, the tick, the 60-tick interval, the wire, or any sim source — this story is additive infra/config/test/docs (AC3).
- **Do NOT** add a `ProjectReference` from the test project to `godot.csproj` (would break the Godot-free guarantee) — keep the `<Compile Include>` model.

---

## Tasks / Subtasks

- [x] **Task 1 — GitHub Actions golden-checksum determinism gate (AC: 1, 3)**
  - [x] Create `.github/workflows/determinism-gate.yml` (net-new). Shape:
    - `on:` `push:` `branches: ['**']`, `pull_request:`, `workflow_dispatch:`
    - `concurrency:` `group: determinism-${{ github.ref }}`, `cancel-in-progress: true` (collapses `[AutoSave]` push bursts)
    - one job `tier1-golden-gate`, `runs-on: windows-latest` (D3)
    - steps: `actions/checkout@v4` → `actions/setup-dotnet@v4` with `dotnet-version: '8.0.x'` → `dotnet restore godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --no-restore`
    - **Do NOT** define `CHIMERA_GOLDEN_RECORD` anywhere (Trap #2). **Do NOT** install Godot or reference the `.sln`/`godot.csproj` (Trap #1). — _Done: `CHIMERA_GOLDEN_RECORD` never set; no Godot install; only the test csproj is referenced (`godot.sln`/`godot.csproj` appear only inside the "do NOT" scope-fence comments)._
  - [x] (Optional) add a final step `actions/upload-artifact@v4` for the `.trx`/test results so failures are inspectable; use `--logger trx` on `dotnet test` if so. — _Done: `--logger "trx;LogFileName=tier1.trx"` + `upload-artifact@v4` with `if: always()` so the `.trx` uploads even when the gate fails._
  - [x] Validate the YAML is well-formed (it is parsed by GitHub; a local YAML lint or a careful read suffices — there is no local Actions runner required). — _Done: `yaml.safe_load` parses cleanly → keys `name`/`on`/`concurrency`/`jobs`; 1 job `tier1-golden-gate`, 5 steps, `runs-on: windows-latest`._

- [x] **Task 2 — Dependency-pinning + isolation guard test (AC: 2)**
  - [x] New `ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs` (create the `Meta/` folder; or place under `Validation/` if you prefer — either is globbed in via `..\` ? **No** — these test files live *in* the test project root tree, which is NOT under `..\src`, so they compile as normal test sources; no csproj change needed). — _Done: `Meta/` created; auto-globbed by the default Microsoft.NET.Sdk compile items; no csproj change for the test source._
  - [x] Locate the two csprojs portably via `[CallerFilePath]` (resolve this test file's dir → `godot/godot.csproj` and the sibling `ProjectChimera.Sim.Tests.csproj`). Mirror `GoldenChecksumReplay.GoldenSourcePath` (`[CallerFilePath]` + `Path.GetDirectoryName` + `Path.Combine`). — _Done: `ResolveFromHere(... [CallerFilePath] ...)` + `Path.GetFullPath(Path.Combine(...))`, mirroring `GoldenSourcePath`._
  - [x] Assert (parse with `XDocument` or a tolerant `Regex`):
    - `godot.csproj` contains a `PackageReference Include="NakamaClient" Version="3.13.0"` (exact version).
    - `godot.csproj` contains **no** `PackageReference` whose `Include` matches `xunit`, `Microsoft.NET.Test.Sdk`, or `*.runner.*` (no test deps in the shipping project).
    - the test `.csproj` **does** contain the test deps (proves isolation is where it should be). — _Done via `XDocument`: 3 facts — exact-pin assert, no-test-dep-leak assert, test-dep-ownership assert._
  - [x] Keep messages actionable (name the offending package + file) so a future drift is self-explaining. Run `dotnet test … --filter FullyQualifiedName~DependencyHygiene` → green. — _Done: messages name the package/version/file + the fix; red-green verified (a deliberately-wrong expected pin made `GodotCsproj_PinsNakamaClient_ToExactVersion` FAIL, then reverted to green)._

- [x] **Task 3 — Reproducibility hardening (AC: 2, 3) [recommended]**
  - [x] Add repo-root `global.json`: `{ "sdk": { "version": "8.0.419", "rollForward": "latestFeature" } }`. Verify `dotnet --version` now resolves an `8.0.x` SDK at the repo root. (D6) — _Done: `dotnet --version` at repo root now reports `8.0.419` (was `9.0.312`)._
  - [x] (Optional, stronger) Enable a lock file on the test project for a fully reproducible package graph: add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` to `ProjectChimera.Sim.Tests.csproj`, run `dotnet restore` once to generate `packages.lock.json`, commit it, and add `--locked-mode` to the CI `dotnet restore` step. (Strengthens AR-2/AR-35 "reproducible".) — _Done: property added, `packages.lock.json` generated under the 8.0 SDK and committed; CI restore uses `--locked-mode`; a local `--locked-mode` restore was verified green._
  - [x] (Optional, hygiene) Harden `.gitignore` so nested build artifacts can never be committed: add unanchored `**/bin/` and `**/obj/` (or `godot/**/bin/`, `godot/**/obj/`). Confirm `git status` still clean and no currently-tracked file is newly ignored. — _Done: added `**/bin/` + `**/obj/`; verified zero tracked files live under any `bin/`/`obj/`, and `packages.lock.json` is NOT swept up by the new patterns._

- [x] **Task 4 — AR-41 telemetry-posture doc (AC: 4)**
  - [x] New `docs/telemetry-posture.md` (or a clearly-titled section in an existing top-level doc): state **NO analytics/telemetry in 1.0**; dev-only diagnostics are **`ILogSink` logs + `.chmr` replay capture** (`ReplayRecorder`/`ReplayPlayer`); the **opt-in crash/desync report** (bundling the `.chmr` + checksum log) and desync-diagnosis tooling (per-system sub-checksums, replay-diff) are a **documented fast-follow, NOT built in 1.0**. Cite AR-41 (`epics.md:234,492`). Build no code. — _Done: `docs/telemetry-posture.md` written; policy + dev-diagnostics + explicit fast-follow sections; zero code._

- [x] **Task 5 — Prove AC3 (no regression; CI is Godot-free; goldens byte-identical) (AC: 3)**
  - [x] Run the **exact CI command** locally with **no** record env var: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` → **all green** (existing suite + the new `DependencyHygieneTests`). Capture the summary line for the Debug Log. — _Done: **Passed! Failed: 0, Passed: 195, Skipped: 0** (`CHIMERA_GOLDEN_RECORD` confirmed empty)._
  - [x] `git status --short -- '*.golden.txt'` → **empty** (four goldens byte-identical). If any moved, you leaked something / set the record var — fix it; never re-record. — _Done: empty._
  - [x] Confirm the workflow installs **no** Godot and references **only** the test `.csproj` (grep the YAML for `godot.sln`/`godot.csproj`/`Godot` → none except the test-project path). — _Done: only hits are the scope-fence comments; the `restore`/`test` steps reference only `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`._
  - [ ] **(DEFERRED to Alec — outward-facing; consumes GitHub Actions minutes.)** Push a branch and confirm the GitHub Actions run goes **green** end-to-end (the real proof the lane works). _Local proof is complete (exact CI command green at 195 tests, goldens byte-identical, workflow verified Godot-free + locked-mode restore green); the **first push to GitHub validates the live lane.** Alec to push and tick this box in the Change Log._ _(Open Question #2 — trigger scope/cost: `windows-latest` bills at 2× minutes; current default is all-branches + `concurrency: cancel-in-progress`. Levers if minutes bite: switch to `ubuntu-latest`, restrict to `master`+PR, or `paths-ignore` doc-only commits.)_

---

## Dev Notes

### Task 1 — the workflow shape (full intent)
This is the load-bearing deliverable. A minimal, correct first version:

```yaml
name: determinism-gate
on:
  push:
    branches: ['**']
  pull_request:
  workflow_dispatch:

concurrency:
  group: determinism-${{ github.ref }}
  cancel-in-progress: true

jobs:
  tier1-golden-gate:
    runs-on: windows-latest        # D3 — primary platform; goldens recorded on Windows. Linux is 1.10c.
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'   # D6 — matches the test project's net8.0 TFM
      - name: Restore (Godot-free Tier-1 only)
        run: dotnet restore godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj
      - name: Golden-checksum determinism gate (fails on any checksum diff)
        run: dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --no-restore
        # CHIMERA_GOLDEN_RECORD is intentionally NEVER set — verify mode asserts against committed goldens.
```

Notes: `-c Release` is convention; goldens are config-independent (pure integer `Fixed` math), so Debug also passes — if in any doubt, run both locally once. `--no-restore` on `test` pairs with the explicit `restore` step (and is required if you later add `--locked-mode`). If you enable the lock file (Task 3), change restore to `dotnet restore … --locked-mode`. Keep the job name stable (`tier1-golden-gate`) — 1.10b/1.10c will add **sibling jobs** to this same workflow file (analyzers; the Linux diff), so a clear name now pays off.

### Task 2 — the guard test (intent; mirror the existing guard-test pattern)
Parse, don't execute. `XDocument.Load(path)` over `godot.csproj`, then LINQ over `//PackageReference`:
- find `NakamaClient` → assert `Version == "3.13.0"`;
- assert no `Include` starts-with `xunit` / equals `Microsoft.NET.Test.Sdk` / contains `.runner.`.
Resolve `path` with a `[CallerFilePath]` helper exactly like `GoldenChecksumReplay.GoldenSourcePath(...)` — from the test source dir, `..` up to `godot/` and join `godot.csproj` (and the test csproj is a known sibling). This runs in CI (source is checked out) and locally with no absolute paths. This is the **permanent** form of AC2 — the CI build incidentally proves the same thing every run, but the test makes it fail a developer's local `dotnet test` the moment a dep drifts.

### Task 3 — why pin the SDK and (optionally) lock packages
Determinism is integer `Fixed` math, so the **checksum** is SDK-independent — `global.json` is **reproducibility hygiene**, not a determinism gate; keep `rollForward` so a machine without the exact patch still builds. A **`packages.lock.json`** is the canonical way to make `dotnet restore` reproducible (it pins the full transitive graph, not just the top-level `xunit`/test-SDK versions) — it directly serves the story's "the dependency surface stays reproducible" goal and also enables `setup-dotnet` package caching later. Both are marked recommended/optional so they don't balloon the story; at minimum do the `global.json`.

### Task 4 — AR-41 is policy, not plumbing
One short doc. The point is that a future contributor (or Alec in six months) can find the explicit "no analytics in 1.0" decision and the note that the crash/desync bundler is deliberately deferred — so nobody "helpfully" adds a telemetry SDK. The diagnostics that *do* exist (logs + `.chmr` replay) are already in the tree; the doc points at them.

### Why the runner is Windows and not Linux (state plainly; don't expand scope)
The goldens were recorded on Windows; Windows is the ship-primary platform. Proving Windows↔Linux *parity* (the real proof `Fixed`-point holds cross-platform) is **AR-37 / Story 1.10c**, which adds the Linux leg via the existing WSL/Ubuntu and **diffs** the two sequences. 1.10a's honest minimal job is "the regression guard runs automatically on the home platform." Keeping the runner Windows avoids importing 1.10c's risk surface into this story.

### Project Structure Notes
- **NEW:** `.github/workflows/determinism-gate.yml`; `godot/ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs`; `global.json` (recommended); `docs/telemetry-posture.md`; optionally `godot/ProjectChimera.Sim.Tests/packages.lock.json`.
- **MODIFIED (optional/hygiene only):** `.gitignore` (nested `bin/obj`); `ProjectChimera.Sim.Tests.csproj` only if enabling the lock file (`RestorePackagesWithLockFile`). **No other csproj edits.**
- **UNCHANGED (must stay so):** all four `*.golden.txt`; `GoldenChecksumReplay.cs` + `GoldenChecksumReplayTests.cs`; `SimChecksum.cs`; the 60-tick interval; **`godot.csproj`'s `NakamaClient 3.13.0` pin** (guarded, not edited); the test project's existing dep set + `<Compile Include>` model; every sim source file.
- **NOT here:** the Linux/WSL cross-platform diff (1.10c), banned-API/AOT analyzers (1.10b), any analytics/crash-bundler code (AR-41 fast-follow), building the Godot game project in CI.

### Project Context Rules
_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_
- **The Godot-free boundary is the whole point.** The Tier-1 project is Godot-free *by design* (AR-35) so the determinism gate — and the future AOT server — compile without the engine. CI must preserve that: target the test `.csproj`, install only the .NET 8 SDK, never add a `ProjectReference` to `godot.csproj`. [Source: project-context.md "The One Architectural Rule" / "Testing"; game-architecture.md AR-35]
- **Reuse, don't fork.** Reuse the existing harness, the existing guard-test pattern (`SimChecksumCoverageGuardTest`/`GodotFreeBoundaryTest`), the existing `[CallerFilePath]` path-resolution, and the existing `.chmr` capture. Add a workflow + one guard test + one doc; build no new systems. [Source: project-context.md "Data layout / reuse"]
- **Determinism is integer math.** No `float`/`System.Random`/`DateTime` enters anything here (there's no sim code at all); the SDK/runtime version does not change the checksum, which is why CI reproducibility is hygiene, not a correctness dependency. [Source: project-context.md "Determinism"]
- **NuGet discipline:** `NakamaClient 3.13.0` is the *only* shipped dep; test deps live only in the Godot-free test project. This story **locks** that, it does not relax it. [Source: project-context.md "Technology Stack"; AR-2]
- **No analytics in 1.0** (AR-41) — dev-only diagnostics via logs + `.chmr`; the crash/desync bundle is a fast-follow. [Source: epics.md:234,492]
- **Engine/runtime:** Godot 4.6.3, .NET 8 (`net8.0`); `ProjectChimera.*`; the project/solution files are `godot.csproj`/`godot.sln` (CI does **not** touch them). [Source: project-context.md "Technology Stack"]

### References
- [Source: epics.md:722-736] — Story 1.10a statement + the two epic ACs (CI runs golden scenarios headless on every push and FAILS on any checksum diff vs the committed golden; NakamaClient pinned 3.13.0 + test-only deps isolated to the Godot-free test project). Covers FR-47, FR-44, AR-2, AR-35; depends on 1.9b; establishes the lane 1.10b/1.10c plug into.
- [Source: epics.md:492] — the **AR-41 fold-in**: "AR-41 (telemetry posture: NO analytics in 1.0 …) is folded into the CI/regression-guard story (1.10a)." [Source: epics.md:234] — full AR-41 text (dev-only diagnostics + opt-in crash/desync report; diagnosis tooling is a fast-follow, not 1.0-blocking).
- [Source: epics.md:179] — **AR-2**: pin the sole shipped NuGet dep to NakamaClient 3.13.0; test-only deps (xUnit) live only in the Godot-free test project (keeps the sim AOT-eligible). [Source: epics.md:226] — **AR-35**: Tier-1 Godot-free plain-.NET `ProjectChimera.Sim.Tests` (xUnit) is the home for the golden-checksum harness and the (deferred) AOT server.
- [Source: epics.md:512-528] — **Story 1.2** (the harness origin): golden-checksum replay harness pinning current behavior; "Establishes the replay regression harness that AR-47/Story 1.10 later wires into CI."
- [Source: epics.md:738-764] — **1.10b** (AR-36 analyzers) and **1.10c** (AR-37 cross-platform via WSL) — the two siblings that plug into this lane; both **out of scope** here. M1 completes when 1.1–1.10 (incl. 1.10a/b/c) are green.
- [Source: prd.md — FR-44, FR-47] — FR-44 two-tier deterministic test infrastructure; FR-47 the CI regression guard that fails the build on a checksum diff (the #1-risk regression backstop). [Source: epics.md:155] — HARD GATE zero-desync / FR-39 (what this guard protects).
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs:25-218] — the reusable engine; `RecordEnvVar=CHIMERA_GOLDEN_RECORD`, `IsRecordMode`, `LoadGolden` (embedded), `GoldenSourcePath` (`[CallerFilePath]` pattern to copy for Task 2), `AlgoVersion` stamped at :183.
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplayTests.cs:18-134] — the 3 ACs; all early-return on `IsRecordMode` (why CI must never set the var).
- [Source: godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj] — Godot-free SDK, `<Compile Include>` sim source, the four embedded goldens (:49-66), the test-dep block (:68-77, already pinned + isolated).
- [Source: godot/godot.csproj:9-12] — `NakamaClient 3.13.0` is the only `PackageReference`; AC2 guards this exact line.
- [Source: 1-9b story (DONE)] — predecessor; ran `dotnet test … ProjectChimera.Sim.Tests.csproj` → 191 green, four goldens byte-identical; the LAN-green this CI guard now protects from silent regression.
- [Source: .gitignore:1-21; `git remote -v`; `dotnet --list-sdks`] — root-anchored `godot/bin|obj` (nested test bin/obj unprotected); remote `ProjectChimera1/Project-Chimera`; SDKs 8.0.419 + 9.0.312 (no `global.json`).

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — `gds-dev-story` workflow.

### Debug Log References

- `dotnet --version` at repo root → **`8.0.419`** (was `9.0.312`; `global.json` now pins the 8.0 family with `rollForward: latestFeature`).
- Lock file generation: `dotnet restore …ProjectChimera.Sim.Tests.csproj` → wrote `packages.lock.json` (4079 bytes); `dotnet restore … --locked-mode` → **restored OK** (CI restore mode verified locally).
- Exact CI test command (verify mode, `CHIMERA_GOLDEN_RECORD=[]`): `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` → **`Passed! Failed: 0, Passed: 195, Skipped: 0, Total: 195`** (191/192 prior + 3 new `DependencyHygieneTests`). 7 pre-existing CS8632 `#nullable` warnings only (out of scope).
- Guard-test teeth check (red→green): temporarily set `ShippedPackageVersion = "9.9.9-TEETH-CHECK"` → `GodotCsproj_PinsNakamaClient_ToExactVersion` **FAILED** with the actionable message; reverted to `3.13.0` → green.
- `git status --short -- '*.golden.txt'` → **empty** (four goldens byte-identical).
- Workflow Godot-free grep: `godot.sln`/`godot.csproj`/`Godot` hits are only in the scope-fence comments; `restore`/`test` steps reference only the test csproj path.
- YAML well-formedness: `yaml.safe_load` parsed cleanly (`name`/`on`/`concurrency`/`jobs`; 1 job, 5 steps, `windows-latest`).
- `.gitignore` safety: `git ls-files | grep -E '(^|/)(bin|obj)/'` → empty (no tracked file newly ignored); `git check-ignore packages.lock.json` → not ignored.

### Completion Notes List

Infrastructure story — **zero sim code**; the 30 Hz tick, `SimChecksum`, the four goldens, and the wire are untouched (AC3). Every change is additive CI/config/test/docs.

- **AC1 ✅** — `.github/workflows/determinism-gate.yml` runs the Tier-1 golden gate headless on `push` (all branches) + `pull_request` + `workflow_dispatch`, on `windows-latest`, installing **only** the .NET 8 SDK (`setup-dotnet 8.0.x`). `CHIMERA_GOLDEN_RECORD` is never set (verify mode). No hardcoded test count — relies on xUnit's non-zero exit. `concurrency: cancel-in-progress` collapses `[AutoSave]` push bursts. `.trx` uploaded as an artifact (even on failure).
- **AC2 ✅** — `Meta/DependencyHygieneTests.cs` (3 facts) locks the surface as a permanent Tier-1 guard: NakamaClient pinned exactly `3.13.0`, no test-only `PackageReference` in `godot.csproj`, and the test deps owned by the test csproj. Csprojs located via `[CallerFilePath]` (mirrors `GoldenChecksumReplay.GoldenSourcePath`). Teeth verified. The csprojs were **not** edited for the pin/isolation (guarded, not changed).
- **AC3 ✅** — exact CI command green locally (195/195); goldens byte-identical; CI installs no Godot and targets only the test csproj.
- **AC4 ✅** — `docs/telemetry-posture.md`: NO analytics/telemetry in 1.0; dev diagnostics = `ILogSink` logs + `.chmr` replay; opt-in crash/desync bundle = documented fast-follow, not built. Zero code.
- **Reproducibility hardening (Task 3, all three done):** repo-root `global.json` (8.0 family); `packages.lock.json` + `--locked-mode` CI restore for a fully pinned transitive graph; `.gitignore` `**/bin/`+`**/obj/` so nested build output can't be committed.
- **Scope fences honored:** no Linux/WSL job or cross-OS diff (1.10c); no analyzers (1.10b); no analytics/crash-bundler code; CI never touches `godot.sln`/`godot.csproj`; no `ProjectReference` from the test project to `godot.csproj`.
- **One item gated on Alec:** the live GitHub Actions run requires a push to his repo (consumes Actions minutes). Local proof is complete; the first push validates the lane. Left unchecked in Task 5 by design (per the story) for Alec to confirm. Surfaced: `windows-latest` bills at 2× minutes (Open Question #2).

### File List

**New:**
- `.github/workflows/determinism-gate.yml` — GitHub Actions determinism gate (AC1).
- `godot/ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs` — dependency-pin + isolation guard test (AC2).
- `global.json` — pins the .NET SDK to the 8.0 family (`rollForward: latestFeature`) for local/CI parity (Task 3).
- `godot/ProjectChimera.Sim.Tests/packages.lock.json` — pinned transitive package graph; CI restores with `--locked-mode` (Task 3).
- `docs/telemetry-posture.md` — AR-41 no-analytics posture (AC4).

**Modified:**
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — added `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` (Task 3). No dependency or compile-item changes.
- `.gitignore` — added unanchored `**/bin/` and `**/obj/` (Task 3 hygiene).
- `Snapshot.md` — session "Next Action" note updated (1.9b → 1.10a). Behavior-neutral session state; declared here per 1.10a code-review.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `1-10a` status tracking (→ in-progress → review → done).

**Unchanged (verified):** all four `*.golden.txt`; `GoldenChecksumReplay.cs`/`GoldenChecksumReplayTests.cs`; `SimChecksum.cs`; `godot/godot.csproj` (NakamaClient `3.13.0` pin guarded, not edited); every sim source file.

### Change Log

- **2026-06-24 — Story 1.10a implemented (Status → review).** Wired the Tier-1 golden-checksum replay harness into CI as a GitHub Actions determinism gate (Godot-free, `windows-latest`, .NET 8 only, fails on any checksum diff); added the `DependencyHygieneTests` guard locking the NakamaClient `3.13.0` pin + test-dep isolation; added reproducibility hardening (`global.json`, `packages.lock.json` + `--locked-mode`, `.gitignore`); documented the AR-41 no-analytics posture. Tier-1 suite **195 green**, four goldens byte-identical, zero sim code changed. Covers FR-47, FR-44, AR-2, AR-35, AR-41, AR-47.
- **PENDING (Alec):** push to GitHub to confirm the live Actions run goes green end-to-end (first push validates the lane; closes the last Task-5 box). _Note: runs on Alec's Actions minutes — `windows-latest` = 2× rate._
- **2026-06-24 — `gds-code-review` PASS → Status `done`.** 3-layer adversarial review (Blind Hunter / Edge Case Hunter / Acceptance Auditor): all 4 ACs met, zero scope-fence breaches, zero spec contradictions, goldens byte-identical. 2 decisions resolved + 5 patches applied — CI SDK pinned to `8.0.419` (matches `global.json`/lock); `pull_request` trigger dropped (kills the per-push double-run; `push:['**']` already covers all branches); dep guard hardened denylist→allowlist (new `GodotCsproj_CarriesExactly_TheSingleShippedPackage`); `if-no-files-found: warn`; `Snapshot.md` added to the File List. Exact CI command re-run green — **Tier-1 196 green** (195 + 1 allowlist test), four goldens byte-identical, verify mode confirmed. The live GitHub Actions run remains Alec's to trigger (unchanged from above; one push validates the lane end-to-end).

---

## Review Findings

_`gds-code-review` (3-layer adversarial: Blind Hunter / Edge Case Hunter / Acceptance Auditor), 2026-06-24, baseline `6b20883`→HEAD. Verdict: **all 4 ACs MET, zero scope-fence breaches, zero spec contradictions, goldens byte-identical, suite 195 green.** Edge Case Hunter empirically re-ran the exact CI `--locked-mode` restore→`-c Release` test sequence locally (green) and ran `git check-ignore` over every tracked file (nothing tracked is swept by the new `**/bin/`+`**/obj/`). Nothing below blocks ship; all items are low-priority hardening or config preference._

### Decisions (resolved 2026-06-24)

- [x] **[Review][Decision] SDK pin** — RESOLVED → pin CI `dotnet-version` to `8.0.419` (match `global.json` + `packages.lock.json`). Alec delegated ("idk what's best for the future"); chosen for a self-consistent, frozen, reproducible toolchain — the gate's whole identity. Determinism is SDK-independent (integer math) so the checksum was never at risk; this is consistency only. _Became a Patch below._
- [x] **[Review][Decision] CI trigger cost** — RESOLVED → drop the `pull_request` trigger (`push:['**']` already covers every branch, so this kills the per-push double-run on PR branches with no loss of coverage). Alec's choice. _Became a Patch below._

### Patches (unambiguous, all low-priority)

- [x] **[Review][Patch] Pin CI SDK to 8.0.419** _(from Decision 1)_ ✅ applied — change `setup-dotnet` `dotnet-version: '8.0.x'` → `'8.0.419'` so CI installs exactly the SDK `global.json`/`packages.lock.json` name; ends the float-vs-pin inconsistency the reviewers flagged. [`.github/workflows/determinism-gate.yml:45`]
- [x] **[Review][Patch] Drop the `pull_request` trigger** _(from Decision 2)_ ✅ applied — remove the `pull_request:` trigger; `push: branches:['**']` already runs the gate on every branch, so this removes the double-run with no coverage loss. [`.github/workflows/determinism-gate.yml:25-26`]
- [x] **[Review][Patch] Strengthen the dep guard from denylist → allowlist** ✅ applied (new `GodotCsproj_CarriesExactly_TheSingleShippedPackage` test) — `IsTestOnlyPackage` is a denylist (`xunit*`/`Microsoft.NET.Test.Sdk`/`.runner.`/`TestPlatform`), so a *non-xunit* test framework added to the shipping project (coverlet, Moq, NUnit, FluentAssertions) would leak through `GodotCsproj_ContainsNo_TestOnlyDependencies` undetected (false-green). Asserting `godot.csproj` carries **exactly one** `PackageReference` (= `NakamaClient`) closes the gap entirely and encodes AR-2's "sole dependency" verbatim. _Optional — the current denylist satisfies AC2 as written; this is a strictly-stronger permanent guard._ [`godot/ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs:191-204,246-250`]
- [x] **[Review][Patch] `upload-artifact` `if-no-files-found: ignore` → `warn`** ✅ applied — if the test host ever crashes before writing `tier1.trx`, the upload step currently passes silently with no diagnostic trail. `warn` surfaces the absence without failing the build (the red `dotnet test` step remains the primary signal). [`.github/workflows/determinism-gate.yml:61`]
- [x] **[Review][Patch] Add `Snapshot.md` to this story's File List** ✅ applied — `Snapshot.md`'s "Next Action" note was edited (1.9b→1.10a) but not declared in the File List. Behavior-neutral; traceability only. [story `### File List`]

### Dismissed (11 — verified false-positive or out-of-scope)

Blind Hunter ran context-free, so several diff-only worries were retired by the project-aware layers: `--no-restore`+`--locked-mode` "brittle" (Edge re-ran it green), gate "may run zero golden tests" (the four goldens + `GoldenChecksumReplayTests`/`MultiFactionGoldenTests`/`GoldenApplierScenarioTests`/`SameTickTieBreakGoldenTests` are all in the test project), `**/obj/` "hides the lock file" (lock lives at project root — `git check-ignore` confirms not ignored), the value-tuple `FirstOrDefault` (correct), `[CallerFilePath]` coupling (verified resolves correctly; fails loud if moved), NU1004 "confusion" (the fail-closed *is* the guard), tag/branch-delete triggers (branch filter handles them), golden line-ending parity (single-OS gate — correctly deferred to 1.10c).
